using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.ViewControllers;
using Hitbloq.Entries;
using Hitbloq.Other;
using Hitbloq.Sources;
using Hitbloq.Utilities;
using HMUI;
using IPA.Utilities;
using IPA.Utilities.Async;
using Tweening;
using UnityEngine;
using Zenject;

namespace Hitbloq.UI.ViewControllers
{
	[HotReload(RelativePathToLayout = @"..\Views\HitbloqPoolListView.bsml")]
	[ViewDefinition("Hitbloq.UI.Views.HitbloqPoolListView.bsml")]
	internal class HitbloqPoolListViewController : BSMLAutomaticViewController, TableView.IDataSource
	{
		public string? poolToOpen;

		[UIComponent("list")]
		private readonly CustomListTableData? _customListTableData = null!;

		[Inject]
		private readonly MaterialGrabber _materialGrabber = null!;

		[Inject]
		private readonly PoolListSource _poolListSource = null!;

		private readonly SemaphoreSlim _poolLoadSemaphore = new(1, 1);

		private readonly List<HitbloqPoolListEntry> _pools = new();

		[Inject]
		private readonly SpriteLoader _spriteLoader = null!;

		[Inject]
		private readonly TimeTweeningManager _uwuTweenyManager = null!;

		private CancellationTokenSource? _fetchCancellationTokenSource;
		private int _fetchRevision;
		private bool _viewActive;
		private bool _refreshNeeded;

		public event Action<HitbloqPoolListEntry>? PoolSelectedEvent;
		public event Action? DetailDismissRequested;

		protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
		{
			base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
			_viewActive = true;

			if (_customListTableData != null)
			{
				BSMLCompat.TableView(_customListTableData).ClearSelection();
			}

			if (_pools.Count == 0 || _refreshNeeded)
			{
				RequestPools();
			}
			else
			{
				OpenPoolToSelect();
			}
		}

		protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
		{
			RetireFetch();
			base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
		}

		protected override void OnDestroy()
		{
			RetireFetch();
			base.OnDestroy();
		}

		private void RetireFetch()
		{
			_viewActive = false;
			_fetchRevision++;
			CancelFetch();
		}

		private void CancelFetch()
		{
			_fetchCancellationTokenSource?.Cancel();
			_fetchCancellationTokenSource?.Dispose();
			_fetchCancellationTokenSource = null;
		}

		private void RequestPools()
		{
			CancelFetch();
			_fetchCancellationTokenSource = new CancellationTokenSource();
			_refreshNeeded = true;
			_ = FetchPools(++_fetchRevision, _fetchCancellationTokenSource.Token);
		}

		private bool IsCurrent(int revision, CancellationToken cancellationToken)
		{
			return this && _viewActive && isActivated && isActiveAndEnabled &&
			       revision == _fetchRevision && !cancellationToken.IsCancellationRequested;
		}

		private async Task FetchPools(int revision, CancellationToken cancellationToken)
		{
			var acquired = false;
			try
			{
				await _poolLoadSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
				acquired = true;
				await UnityGame.SwitchToMainThreadAsync();
				if (!IsCurrent(revision, cancellationToken) || _customListTableData == null)
				{
					return;
				}

				BSMLCompat.TableView(_customListTableData).ClearSelection();
				Loaded = false;
				var fetchedPools = await _poolListSource.GetAsync(cancellationToken).ConfigureAwait(false);
				await UnityGame.SwitchToMainThreadAsync();
				if (fetchedPools == null)
				{
					return;
				}

				while (IsCurrent(revision, cancellationToken))
				{
					var originals = fetchedPools.ToArray();
					var keys = new PoolSortKey[originals.Length];
					for (var i = 0; i < originals.Length; i++)
					{
						keys[i] = new PoolSortKey(originals[i]);
					}
					var request = new PoolSortRequest(keys, _sortOption, _sortDescending);
					var physicalTask = Task.Factory.StartNew(PoolSortRequest.Process, request,
						CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
					var indices = await physicalTask.ConfigureAwait(false);
					await UnityGame.SwitchToMainThreadAsync();
					if (!IsCurrent(revision, cancellationToken))
					{
						return;
					}
					if (!SnapshotMatches(fetchedPools, originals, keys))
					{
						continue;
					}

					if (request.Sorts || request.Descending)
					{
						for (var i = 0; i < indices.Length; i++)
						{
							fetchedPools[i] = originals[indices[i]];
						}
						// Empty sorts still invalidate existing enumerators.
						if (indices.Length == 0)
						{
							fetchedPools.Reverse(0, 0);
						}
					}
					_pools.Clear();
					_pools.AddRange(fetchedPools);
					_refreshNeeded = false;
					break;
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
			}
			finally
			{
				if (acquired)
				{
					_poolLoadSemaphore.Release();
					await FinishFetch(revision, cancellationToken);
				}
			}
		}

		private async Task FinishFetch(int revision, CancellationToken cancellationToken)
		{
			await UnityGame.SwitchToMainThreadAsync();
			if (!IsCurrent(revision, cancellationToken) || _customListTableData == null)
			{
				return;
			}
			Loaded = true;
			await SiraUtil.Extras.Utilities.PauseChamp;
			await UnityGame.SwitchToMainThreadAsync();
			if (!IsCurrent(revision, cancellationToken))
			{
				return;
			}
			BSMLCompat.TableView(_customListTableData).ReloadData();
			if (!IsCurrent(revision, cancellationToken))
			{
				return;
			}
			DetailDismissRequested?.Invoke();
			if (IsCurrent(revision, cancellationToken))
			{
				OpenPoolToSelect();
			}
		}

		private static bool SnapshotMatches(List<HitbloqPoolListEntry> pools,
			HitbloqPoolListEntry[] originals, PoolSortKey[] keys)
		{
			if (pools.Count != originals.Length)
			{
				return false;
			}
			for (var i = 0; i < originals.Length; i++)
			{
				if (!ReferenceEquals(pools[i], originals[i]) || !keys[i].Matches(pools[i]))
				{
					return false;
				}
			}
			return true;
		}

		private readonly struct PoolSortKey
		{
			public readonly bool Present;
			public readonly string? Title;
			public readonly int PlayerCount;
			public readonly int Popularity;

			public PoolSortKey(HitbloqPoolListEntry? entry)
			{
				Present = entry != null;
				Title = entry?.Title;
				PlayerCount = entry?.PlayerCount ?? 0;
				Popularity = entry?.Popularity ?? 0;
			}

			public bool Matches(HitbloqPoolListEntry? entry)
			{
				return entry == null ? !Present : Present &&
					string.Equals(Title, entry.Title, StringComparison.Ordinal) &&
					PlayerCount == entry.PlayerCount && Popularity == entry.Popularity;
			}
		}

		private sealed class PoolSortRequest : IComparer<int>
		{
			private readonly PoolSortKey[] _keys;
			private readonly string _option;
			public readonly bool Descending;
			public bool Sorts => _option == "Popularity" || _option == "Player Count" || _option == "Alphabetical";

			public PoolSortRequest(PoolSortKey[] keys, string option, bool descending)
			{
				_keys = keys;
				_option = option;
				Descending = descending;
			}

			public int Compare(int x, int y)
			{
				var left = _keys[x];
				var right = _keys[y];
				if (!left.Present)
				{
					return right.Present ? -1 : 0;
				}
				if (!right.Present)
				{
					return 1;
				}
				return _option switch
				{
					"Popularity" => left.Popularity.CompareTo(right.Popularity),
					"Player Count" => left.PlayerCount.CompareTo(right.PlayerCount),
					"Alphabetical" => string.Compare(left.Title, right.Title, StringComparison.Ordinal),
					_ => 0
				};
			}

			public static int[] Process(object state)
			{
				var request = (PoolSortRequest) state;
				var indices = new int[request._keys.Length];
				for (var i = 0; i < indices.Length; i++)
				{
					indices[i] = i;
				}
				if (request.Sorts)
				{
					Array.Sort(indices, request);
				}
				if (request.Descending)
				{
					Array.Reverse(indices);
				}
				return indices;
			}
		}

		private void OpenPoolToSelect()
		{
			if (this && _viewActive && isActivated && isActiveAndEnabled && !_refreshNeeded && poolToOpen != null && _customListTableData != null)
			{
				for (var i = 0; i < _pools.Count; i++)
				{
					if (_pools[i].ID == poolToOpen)
					{
						var index = i;
						var entry = _pools[index];
						var revision = _fetchRevision;
						_ = UnityMainThreadTaskScheduler.Factory.StartNew(() =>
						{
							if (_refreshNeeded || !IsCurrent(revision, CancellationToken.None) || index >= _pools.Count || !ReferenceEquals(entry, _pools[index]))
							{
								return;
							}
							BSMLCompat.TableView(_customListTableData).SelectCellWithIdx(index);
							if (_refreshNeeded || !IsCurrent(revision, CancellationToken.None))
							{
								return;
							}
							BSMLCompat.TableView(_customListTableData).ScrollToCellWithIdx(index, TableView.ScrollPositionType.Center, true);
							PoolSelectedEvent?.Invoke(entry);
						});
						break;
					}
				}

				poolToOpen = null;
			}
		}

		[UIAction("#post-parse")]
		private void PostParse()
		{
			rectTransform.anchorMin = new Vector2(0.5f, 0);
			rectTransform.localPosition = Vector3.zero;
			if (_customListTableData != null)
			{
				BSMLCompat.TableView(_customListTableData).SetDataSource(this, true);
			}
		}

		[UIAction("list-select")]
		private void OnListSelect(TableView _, int index)
		{
			PoolSelectedEvent?.Invoke(_pools[index]);
		}

		#region Sorting

		private string _sortOption = "Popularity";
		private bool _sortDescending = true;

		[UIAction("sort-selected")]
		private void SortSelected(string sortOption)
		{
			_sortOption = sortOption;
			RequestPools();
		}

		[UIAction("toggle-sort-direction")]
		private void ToggleSortDirection()
		{
			_sortDescending = !_sortDescending;
			NotifyPropertyChanged(nameof(SortDirection));

			RequestPools();
		}

		[UIValue("sort-options")]
		private List<object> _sortOptions = new() {"Popularity", "Player Count", "Alphabetical"};

		[UIValue("sort-direction")]
		private string SortDirection => _sortDescending ? "▼" : "▲";

		#endregion

		#region Loading

		private bool _loaded;

		[UIValue("is-loading")]
		public bool IsLoading => !Loaded;

		[UIValue("has-loaded")]
		public bool Loaded
		{
			get => _loaded;
			set
			{
				_loaded = value;
				NotifyPropertyChanged();
				NotifyPropertyChanged(nameof(IsLoading));
			}
		}

		#endregion

		#region TableData

		private const string KReuseIdentifier = "HitbloqPoolCell";

		private HitbloqPoolCellController GetCell()
		{
			var tableCell = BSMLCompat.TableView(_customListTableData!).DequeueReusableCellForIdentifier(KReuseIdentifier);

			if (tableCell == null)
			{
				var hitbloqPoolCell = new GameObject(nameof(HitbloqPoolCellController), typeof(Touchable)).AddComponent<HitbloqPoolCellController>();
				hitbloqPoolCell.SetRequiredUtils(_spriteLoader, _materialGrabber, _uwuTweenyManager);
				tableCell = hitbloqPoolCell;
				tableCell.interactable = true;

				tableCell.reuseIdentifier = KReuseIdentifier;
				BSMLCompat.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(Assembly.GetExecutingAssembly(), "Hitbloq.UI.Views.HitbloqPoolCell.bsml"), tableCell.gameObject, tableCell);
			}

			return (HitbloqPoolCellController) tableCell;
		}

		public float CellSize(int idx)
		{
			return 23;
		}

#if HITBLOQ_BS_1_29_1
		public float CellSize()
		{
			return 23;
		}
#endif

		public int NumberOfCells()
		{
			return _pools.Count;
		}

		public TableCell CellForIdx(TableView tableView, int idx)
		{
			return GetCell().PopulateCell(_pools[idx]);
		}

		#endregion
	}
}
