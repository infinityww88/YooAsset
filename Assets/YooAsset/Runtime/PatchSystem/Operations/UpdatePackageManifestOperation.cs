﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace YooAsset
{
	/// <summary>
	/// 向远端请求并更新补丁清单
	/// </summary>
	public abstract class UpdatePackageManifestOperation : AsyncOperationBase
	{
		/// <summary>
		/// 是否发现了新的补丁清单
		/// </summary>
		public bool FoundNewManifest { protected set; get; } = false;
	}

	/// <summary>
	/// 编辑器下模拟运行的更新清单操作
	/// </summary>
	internal sealed class EditorPlayModeUpdatePackageManifestOperation : UpdatePackageManifestOperation
	{
		internal override void Start()
		{
			Status = EOperationStatus.Succeed;
		}
		internal override void Update()
		{
		}
	}

	/// <summary>
	/// 离线模式的更新清单操作
	/// </summary>
	internal sealed class OfflinePlayModeUpdatePackageManifestOperation : UpdatePackageManifestOperation
	{
		internal override void Start()
		{
			Status = EOperationStatus.Succeed;
		}
		internal override void Update()
		{
		}
	}

	/// <summary>
	/// 联机模式的更新清单操作
	/// 注意：优先比对沙盒清单哈希值，如果有变化就更新远端清单文件，并保存到本地。
	/// </summary>
	internal sealed class HostPlayModeUpdatePackageManifestOperation : UpdatePackageManifestOperation
	{
		private enum ESteps
		{
			None,
			TryLoadCacheHash,
			DownloadWebHash,
			CheckDownloadWebHash,
			DownloadWebManifest,
			CheckDownloadWebManifest,
			CheckDeserializeWebManifest,
			StartVerifyOperation,
			CheckVerifyOperation,
			Done,
		}

		private static int RequestCount = 0;
		private readonly HostPlayModeImpl _impl;
		private readonly string _packageName;
		private readonly string _packageVersion;
		private readonly int _timeout;
		private UnityWebDataRequester _downloader1;
		private UnityWebDataRequester _downloader2;
		private DeserializeManifestOperation _deserializer;
		private CacheFilesVerifyOperation _verifyOperation;

		private string _cacheManifestHash;
		private ESteps _steps = ESteps.None;
		private float _verifyTime;

		internal HostPlayModeUpdatePackageManifestOperation(HostPlayModeImpl impl, string packageName, string packageVersion, int timeout)
		{
			_impl = impl;
			_packageName = packageName;
			_packageVersion = packageVersion;
			_timeout = timeout;
		}
		internal override void Start()
		{
			RequestCount++;
			_steps = ESteps.TryLoadCacheHash;
		}
		internal override void Update()
		{
			if (_steps == ESteps.None || _steps == ESteps.Done)
				return;

			if (_steps == ESteps.TryLoadCacheHash)
			{
				string filePath = PersistentHelper.GetCacheManifestFilePath(_packageName);
				if (File.Exists(filePath))
				{
					_cacheManifestHash = HashUtility.FileMD5(filePath);
					_steps = ESteps.DownloadWebHash;
				}
				else
				{
					_steps = ESteps.DownloadWebManifest;
				}
			}

			if (_steps == ESteps.DownloadWebHash)
			{
				string fileName = YooAssetSettingsData.GetPatchManifestHashFileName(_packageName, _packageVersion);
				string webURL = GetPatchManifestRequestURL(fileName);
				YooLogger.Log($"Beginning to request patch manifest hash : {webURL}");
				_downloader1 = new UnityWebDataRequester();
				_downloader1.SendRequest(webURL, _timeout);
				_steps = ESteps.CheckDownloadWebHash;
			}

			if (_steps == ESteps.CheckDownloadWebHash)
			{
				if (_downloader1.IsDone() == false)
					return;

				if (_downloader1.HasError())
				{
					_steps = ESteps.Done;
					Status = EOperationStatus.Failed;
					Error = _downloader1.GetError();
				}
				else
				{
					string webManifestHash = _downloader1.GetText();
					if (_cacheManifestHash == webManifestHash)
					{
						YooLogger.Log($"Not found new package : {_packageName}");
						_steps = ESteps.Done;
						Status = EOperationStatus.Succeed;
					}
					else
					{
						YooLogger.Log($"Package {_packageName} is change : {_cacheManifestHash} -> {webManifestHash}");
						_steps = ESteps.DownloadWebManifest;
					}
				}
				_downloader1.Dispose();
			}

			if (_steps == ESteps.DownloadWebManifest)
			{
				string fileName = YooAssetSettingsData.GetPatchManifestBinaryFileName(_packageName, _packageVersion);
				string webURL = GetPatchManifestRequestURL(fileName);
				YooLogger.Log($"Beginning to request patch manifest : {webURL}");
				_downloader2 = new UnityWebDataRequester();
				_downloader2.SendRequest(webURL, _timeout);
				_steps = ESteps.CheckDownloadWebManifest;
			}

			if (_steps == ESteps.CheckDownloadWebManifest)
			{
				if (_downloader2.IsDone() == false)
					return;

				if (_downloader2.HasError())
				{
					_steps = ESteps.Done;
					Status = EOperationStatus.Failed;
					Error = _downloader2.GetError();
				}
				else
				{
					// 保存文件到沙盒内
					byte[] bytesData = _downloader2.GetData();
					string savePath = PersistentHelper.GetCacheManifestFilePath(_packageName);
					FileUtility.CreateFile(savePath, bytesData);

					// 解析二进制数据
					_deserializer = new DeserializeManifestOperation(bytesData);
					OperationSystem.StartOperation(_deserializer);
					_steps = ESteps.CheckDeserializeWebManifest;
				}
				_downloader2.Dispose();
			}

			if (_steps == ESteps.CheckDeserializeWebManifest)
			{
				Progress = _deserializer.Progress;
				if (_deserializer.IsDone)
				{
					if (_deserializer.Status == EOperationStatus.Succeed)
					{
						_impl.SetLocalPatchManifest(_deserializer.Manifest);
						FoundNewManifest = true;
						_steps = ESteps.StartVerifyOperation;
					}
					else
					{
						Status = EOperationStatus.Failed;
						Error = _deserializer.Error;
						_steps = ESteps.Done;
					}
				}
			}

			if (_steps == ESteps.StartVerifyOperation)
			{
#if UNITY_WEBGL
				_verifyOperation = new CacheFilesVerifyWithoutThreadOperation(_impl.LocalPatchManifest, _impl.QueryServices);
#else
				_verifyOperation = new CacheFilesVerifyWithThreadOperation(_impl.LocalPatchManifest, _impl.QueryServices);
#endif

				OperationSystem.StartOperation(_verifyOperation);
				_verifyTime = UnityEngine.Time.realtimeSinceStartup;
				_steps = ESteps.CheckVerifyOperation;
			}

			if (_steps == ESteps.CheckVerifyOperation)
			{
				Progress = _verifyOperation.Progress;
				if (_verifyOperation.IsDone)
				{
					_steps = ESteps.Done;
					Status = EOperationStatus.Succeed;
					float costTime = UnityEngine.Time.realtimeSinceStartup - _verifyTime;
					YooLogger.Log($"Verify result : Success {_verifyOperation.VerifySuccessList.Count}, Fail {_verifyOperation.VerifyFailList.Count}, Elapsed time {costTime} seconds");
				}
			}
		}

		/// <summary>
		/// 获取补丁清单请求地址
		/// </summary>
		private string GetPatchManifestRequestURL(string fileName)
		{
			// 轮流返回请求地址
			if (RequestCount % 2 == 0)
				return _impl.GetPatchDownloadFallbackURL(fileName);
			else
				return _impl.GetPatchDownloadMainURL(fileName);
		}
	}
}