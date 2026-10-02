using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Components.Settings;
using BeatSaberMarkupLanguage.ViewControllers;
using Hitbloq.Configuration;
using Hitbloq.Entries;
using Hitbloq.Interfaces;
using Hitbloq.Other;
using Hitbloq.Sources;
using Hitbloq.Utilities;
using HMUI;
using IPA.Utilities;
using UnityEngine;
using Zenject;

namespace Hitbloq.UI.ViewControllers
{
	[HotReload(RelativePathToLayout = @"..\Views\HitbloqPanel.bsml")]
	[ViewDefinition("Hitbloq.UI.Views.HitbloqPanel.bsml")]
	internal class HitbloqPanelController : BSMLAutomaticViewController, IInitializable, IDisposable, INotifyUserRegistered, IBeatmapKeyUpdater, IPoolUpdater, ILeaderboardEntriesUpdater
	{
		private readonly Color _cancelHighlightColor = Color.red;

		[UIComponent("container")]
		private readonly Backgroundable? _container = null!;

		[UIComponent("dropdown-list")]
		private readonly DropDownListSetting? _dropDownListSetting = null!;

		[UIComponent("dropdown-list")]
		private readonly RectTransform? _dropDownListTransform = null!;

		[Inject]
		private readonly IEventSource _eventSource = null!;

		[Inject]
		private readonly MainFlowCoordinator _mainFlowCoordinator = null!;

		[InjectOptional]
		private readonly PlaylistManagerIHardlyKnowHer? _playlistManagerIHardlyKnowHer = null!;

		[UIComponent("pm-image")]
		private readonly ClickableImage? _playlistManagerImage = null!;

		[Inject]
		private readonly PoolInfoSource _poolInfoSource = null!;

		[Inject]
		private readonly PoolListSource _poolListSource = null!;

		[Inject]
		private readonly RankInfoSource _rankInfoSource = null!;

		private bool _cuteMode;

		private Color? _defaultHighlightColour;
		private bool _downloadingActive;
		private bool _eventActive;

		[UIComponent("event-image")]
		private ImageView? _eventImage;

		private Sprite? _flushedSprite;
		private bool _loadingActive;

		[UIComponent("hitbloq-logo")]
		private ImageView? _logo;

		private Sprite? _logoSprite;

		private CancellationTokenSource? _poolInfoTokenSource;
		private List<string>? _poolNames;
		private readonly SemaphoreSlim _optionPreparationSemaphore = new(1, 1);
		private int _optionRevision;
		private int _rankRevision;
		private bool _disposed;
		private bool _dropdownReady;
		private bool _optionsPending;

		[UIValue("pools")] private List<object> _pools = new() {"None"};
		private string _promptText = "";

		private HitbloqRankInfo? _rankInfo;
		private CancellationTokenSource? _rankInfoTokenSource;
		private string? _selectedPool;

		[UIComponent("separator")]
		private ImageView? _separator;

		private bool CuteMode
		{
			get => _cuteMode;
			set
			{
				if (_cuteMode != value)
				{
					if (_logo != null)
					{
						_logo.sprite = value ? _flushedSprite : _logoSprite;
						var hoverHint = _logo.GetComponent<HoverHint>();
						hoverHint.text = value ? "Pink Cute!" : "Open Hitbloq Menu";
					}
				}

				_cuteMode = value;
			}
		}

		[UIValue("prompt-text")]
		public string PromptText
		{
			get => _promptText;
			set
			{
				_promptText = value;
				NotifyPropertyChanged();
			}
		}

		[UIValue("loading-active")]
		public bool LoadingActive
		{
			get => _loadingActive;
			set
			{
				_loadingActive = value;
				NotifyPropertyChanged();
			}
		}

		[UIValue("downloading-active")]
		private bool DownloadingActive
		{
			get => _downloadingActive;
			set
			{
				_downloadingActive = value;

				if (_playlistManagerImage != null && _defaultHighlightColour != null)
				{
					_playlistManagerImage.HighlightColor = value ? _cancelHighlightColor : _defaultHighlightColour.Value;
				}

				NotifyPropertyChanged();
				NotifyPropertyChanged(nameof(PlaylistManagerHoverHint));
			}
		}

		[UIValue("pool-ranking-text")]
		private string PoolRankingText =>
			$"<b>Pool Ranking:</b> #{_rankInfo?.Rank} <size=75%>(<color=#aa6eff>{_rankInfo?.CR.ToString("F2")}cr</color>)";

		[UIValue("pm-active")]
		private bool PlaylistManagerActive => _playlistManagerIHardlyKnowHer != null && _mainFlowCoordinator.YoungestChildFlowCoordinatorOrSelf() is SinglePlayerLevelSelectionFlowCoordinator;

		[UIValue("pm-hover")]
		private string PlaylistManagerHoverHint =>
			DownloadingActive ? "Cancel playlist download" : "Open the playlist for this pool.";

		[UIValue("event-active")]
		private bool EventActive
		{
			get => _eventActive;
			set
			{
				_eventActive = value;
				NotifyPropertyChanged();
			}
		}

		public void BeatmapKeyUpdated(BeatmapKey beatmapKey, HitbloqLevelInfo? levelInfoEntry)
		{
			_ = BeatmapKeyUpdatedAsync(levelInfoEntry);
		}

		public void Dispose()
		{
			_disposed = true;
			_dropdownReady = false;
			_optionRevision++;
			_poolInfoTokenSource?.Cancel();
			_poolInfoTokenSource?.Dispose();
			_poolInfoTokenSource = null;
			RetireRank();
			if (_playlistManagerIHardlyKnowHer != null)
			{
				_playlistManagerIHardlyKnowHer.HitbloqPlaylistSelected -= OnPlaylistSelected;
			}
		}

		protected override void OnDestroy()
		{
			Dispose();
			base.OnDestroy();
		}

		public void Initialize()
		{
			if (_playlistManagerIHardlyKnowHer != null)
			{
				_playlistManagerIHardlyKnowHer.HitbloqPlaylistSelected += OnPlaylistSelected;
			}
		}

		public void LeaderboardEntriesUpdated(List<HitbloqMapLeaderboardEntry>? leaderboardEntries)
		{		
			CuteMode = leaderboardEntries != null && leaderboardEntries.Exists(u => u.UserID == 726);
		}

		public void UserRegistered()
		{
			PromptText = "";
			LoadingActive = false;
		}

		public void PoolUpdated(string pool)
		{
			_ = PoolUpdatedAsync(pool);
		}

		public event Action<string>? PoolChangedEvent;
		public event Action<HitbloqRankInfo, string>? RankTextClickedEvent;
		public event Action? LogoClickedEvent;
		public event Action? EventClickedEvent;

		[UIAction("#post-parse")]
		private async Task PostParse()
		{
			// Background related stuff
			if (BSMLCompat.Background(_container!) is ImageView background)
			{
				background.material = BeatSaberMarkupLanguage.Utilities.ImageResources.NoGlowMat;
				background.color0 = Color.white;
				background.color1 = new Color(1f, 1f, 1f, 0f);
				background.color = Color.gray;
				Accessors.GradientAccessor(ref background) = true;
				Accessors.SkewAccessor(ref background) = 0.18f;
			}

			// Loading up logos
			_logoSprite = await BSMLCompat.LoadSpriteFromAssemblyAsync("Hitbloq.Images.Logo.png");
			_flushedSprite = await BSMLCompat.LoadSpriteFromAssemblyAsync("Hitbloq.Images.LogoFlushed.png");
			_logo!.sprite = CuteMode ? _flushedSprite : _logoSprite;

			Accessors.SkewAccessor(ref _logo) = 0.18f;
			_logo.SetVerticesDirty();

			Accessors.SkewAccessor(ref _separator!) = 0.18f;
			_separator.SetVerticesDirty();

			// Dropdown needs to be modified to look good
			var dropdownText = _dropDownListTransform!.GetComponentInChildren<CurvedTextMeshPro>();
			dropdownText.fontSize = 3.5f;
			dropdownText.transform.localPosition = new Vector3(-1.5f, 0, 0);

			// A bit of explanation of what is going on
			// I want to make a maximum of 2 cells visible, however I first need to parse exactly 2 cells and clean them up
			// After that I populate the current pool options
			if (_dropDownListSetting == null)
				throw new InvalidOperationException("Hitbloq pool dropdown was not bound.");
			BSMLCompat.Dropdown(_dropDownListSetting).SetField("_numberOfVisibleCells", 2);
			BSMLCompat.SetValues(_dropDownListSetting, new List<object> {"1", "2"});
			_dropDownListSetting.UpdateChoices();
			var poolIndex = _optionsPending ? 0 : _poolNames?.IndexOf(_selectedPool ?? "") ?? 0;
			_dropdownReady = true;
			RefreshDropdown(poolIndex, _optionRevision, false);

			_defaultHighlightColour = _playlistManagerImage!.HighlightColor;

			_ = FetchEvent();
		}

		private async Task FetchEvent()
		{
			var hitbloqEvent = await _eventSource.GetAsync();
			if (hitbloqEvent != null && hitbloqEvent.ID != -1)
			{
				EventActive = true;
				Accessors.SkewAccessor(ref _eventImage!) = 0.18f;
			}
		}

		protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
		{
			base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
			NotifyPropertyChanged(nameof(PlaylistManagerActive));
		}

		protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
		{
			if (_dropDownListSetting != null)
			{
				BSMLCompat.Dropdown(_dropDownListSetting).Hide(false);
			}

			base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
		}

		[UIAction("pool-changed")]
		private void PoolChanged(string formattedPool)
		{
			if (!_disposed && _dropdownReady && !_optionsPending && _dropDownListSetting != null && _poolNames != null)
			{
				var index = BSMLCompat.Dropdown(_dropDownListSetting).selectedIndex;
				if (index >= 0 && index < _poolNames.Count)
				{
					PoolChangedEvent?.Invoke(_poolNames[index]);
				}
			}
		}

		[UIAction("clicked-rank-text")]
		private void RankTextClicked()
		{
			if (!_disposed && _dropdownReady && !_optionsPending && _dropDownListSetting != null && _rankInfo != null && _poolNames != null)
			{
				var index = BSMLCompat.Dropdown(_dropDownListSetting).selectedIndex;
				if (index >= 0 && index < _poolNames.Count)
				{
					RankTextClickedEvent?.Invoke(_rankInfo, _poolNames[index]);
				}
			}
		}

		[UIAction("pm-click")]
		private void PlaylistManagerClicked()
		{
			if (PlaylistManagerActive && _selectedPool != null)
			{
				DownloadingActive = _playlistManagerIHardlyKnowHer!.IsDownloading;
				if (DownloadingActive)
				{
					_playlistManagerIHardlyKnowHer.CancelDownload();
				}
				else
				{
					_playlistManagerIHardlyKnowHer.DownloadOrOpenPlaylist(_selectedPool, () => DownloadingActive = false);
				}

				DownloadingActive = _playlistManagerIHardlyKnowHer.IsDownloading;
			}
		}

		[UIAction("logo-click")]
		private void LogoClicked()
		{
			LogoClickedEvent?.Invoke();
		}

		[UIAction("event-click")]
		private void EventClicked()
		{
			EventClickedEvent?.Invoke();
		}

		private void OnPlaylistSelected(string pool)
		{
			_selectedPool = pool;
		}

		private async Task BeatmapKeyUpdatedAsync(HitbloqLevelInfo? levelInfoEntry)
		{
			if (_disposed || !this)
			{
				return;
			}
			_poolInfoTokenSource?.Cancel();
			_poolInfoTokenSource?.Dispose();
			_poolInfoTokenSource = new CancellationTokenSource();
			var cancellationToken = _poolInfoTokenSource.Token;
			var revision = ++_optionRevision;
			_optionsPending = true;
			_pools = new List<object>();
			RetireRank();
			var poolSnapshot = levelInfoEntry?.Pools.ToArray();
			var acquired = false;

			try
			{
				await _optionPreparationSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				await UnityGame.SwitchToMainThreadAsync();
				if (!IsOptionsCurrent(revision, cancellationToken))
				{
					return;
				}

				var names = new List<string>();
				var labels = new List<object>();
				if (poolSnapshot != null)
				{
					var detailedPools = await _poolListSource.GetAsync(cancellationToken).ConfigureAwait(false);
					await UnityGame.SwitchToMainThreadAsync();
					if (!IsOptionsCurrent(revision, cancellationToken))
					{
						return;
					}
					var popularities = detailedPools?.Select(pool => new KeyValuePair<string, int>(pool.ID, pool.Popularity)).ToArray();
					var sortRequest = new PoolOptionPreparation.SortRequest(poolSnapshot, popularities);
					var sortTask = Task.Factory.StartNew(PoolOptionPreparation.SortRequest.Process, sortRequest,
						CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
					var pools = await sortTask.ConfigureAwait(false);
					await UnityGame.SwitchToMainThreadAsync();
					if (!IsOptionsCurrent(revision, cancellationToken))
					{
						return;
					}

					foreach (var pool in pools)
					{
						var poolInfo = await _poolInfoSource.GetPoolInfoAsync(pool.Key, cancellationToken).ConfigureAwait(false);
						await UnityGame.SwitchToMainThreadAsync();
						if (!IsOptionsCurrent(revision, cancellationToken))
						{
							return;
						}
						var shownName = poolInfo?.ShownName;
						var numberFormat = poolInfo != null && shownName == null ? null :
							NumberFormatInfo.ReadOnly((NumberFormatInfo) NumberFormatInfo.CurrentInfo.Clone());
						var labelRequest = new PoolOptionPreparation.LabelRequest(pool.Key, pool.Value, shownName, poolInfo != null, numberFormat);
						var labelTask = Task.Factory.StartNew(PoolOptionPreparation.LabelRequest.Process, labelRequest,
							CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
						var label = await labelTask.ConfigureAwait(false);
						await UnityGame.SwitchToMainThreadAsync();
						if (!IsOptionsCurrent(revision, cancellationToken))
						{
							return;
						}
						names.Add(pool.Key);
						labels.Add(label);
					}
				}

				if (names.Count == 0)
				{
					names.Add("None");
				}
				_poolNames = names;
				_pools = labels;
				_optionsPending = false;
				var selected = _selectedPool;
				if (PluginConfig.Instance.PrioritisePlaylistPool && _playlistManagerIHardlyKnowHer is {SelectedPlaylist: not null})
				{
					selected = PlaylistManagerIHardlyKnowHer.GetPlaylistPool(_playlistManagerIHardlyKnowHer.SelectedPlaylist);
				}
				var poolIndex = names.IndexOf(selected ?? "");
				PoolChangedEvent?.Invoke(names[poolIndex == -1 ? 0 : poolIndex]);
				if (IsOptionsCurrent(revision, cancellationToken))
				{
					RefreshDropdown(poolIndex, revision);
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			finally
			{
				if (acquired)
				{
					_optionPreparationSemaphore.Release();
				}
			}
		}

		private bool IsOptionsCurrent(int revision, CancellationToken cancellationToken)
		{
			return !_disposed && this && revision == _optionRevision && !cancellationToken.IsCancellationRequested;
		}

		private void RefreshDropdown(int poolIndex, int revision, bool clearPrompt = true)
		{
			if (!_dropdownReady || _dropDownListSetting == null || !IsOptionsCurrent(revision, CancellationToken.None))
			{
				return;
			}
			BSMLCompat.SetValues(_dropDownListSetting, _pools.Count != 0 ? _pools : new List<object> {"None"});
			_dropDownListSetting.UpdateChoices();
			if (!IsOptionsCurrent(revision, CancellationToken.None))
			{
				return;
			}
			BSMLCompat.Dropdown(_dropDownListSetting).SelectCellWithIdx(poolIndex == -1 ? 0 : poolIndex);
			if (clearPrompt && IsOptionsCurrent(revision, CancellationToken.None) &&
				!LoadingActive && !PromptText.Contains("<color=red>") && !PromptText.Contains("<color=green>"))
			{
				PromptText = "";
			}
		}

		private void RetireRank(bool clear = true)
		{
			_rankRevision++;
			_rankInfoTokenSource?.Cancel();
			_rankInfoTokenSource?.Dispose();
			_rankInfoTokenSource = null;
			if (clear)
			{
				_rankInfo = null;
			}
		}

		private async Task PoolUpdatedAsync(string pool)
		{
			if (_disposed || !this)
			{
				return;
			}
			RetireRank(false);
			_rankInfoTokenSource = new CancellationTokenSource();
			var cancellationToken = _rankInfoTokenSource.Token;
			var revision = _rankRevision;
			var optionRevision = _optionRevision;
			_selectedPool = pool;
			var rankInfo = await _rankInfoSource.GetRankInfoForSelfAsync(pool, cancellationToken).ConfigureAwait(false);
			await UnityGame.SwitchToMainThreadAsync();
			if (IsOptionsCurrent(optionRevision, cancellationToken) && revision == _rankRevision && _selectedPool == pool)
			{
				_rankInfo = rankInfo;
				NotifyPropertyChanged(nameof(PoolRankingText));
			}
		}
	}
}
