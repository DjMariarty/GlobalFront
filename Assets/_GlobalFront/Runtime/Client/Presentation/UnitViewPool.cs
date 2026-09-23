using System;
using System.Collections.Generic;
using GlobalFront.Client.Replication;
using GlobalFront.Core.Model;
using UnityEngine;

namespace GlobalFront.Client.Presentation
{
    /// <summary>
    /// Pre-allocated pool of <see cref="UnitView"/> instances, kept per
    /// <see cref="UnitKinds"/> archetype (Phase 3, step 3.3, OD-26).
    ///
    /// Combat must never instantiate or destroy a unit object: at 400 live units an
    /// <c>Instantiate</c> per spawn is a frame spike plus a garbage burst, and a
    /// <c>Destroy</c> is a deferred cleanup that leaves corpses in the hierarchy for
    /// a frame. Every view therefore comes from <see cref="Warmup"/>, which runs on
    /// load, and then cycles through <see cref="Acquire"/> and
    /// <see cref="Release"/> for the whole match.
    ///
    /// The steady-state path allocates nothing by construction: one
    /// <see cref="Stack{T}"/> per archetype is grown by the warm-up (so
    /// <c>Push</c>/<c>Pop</c> stop resizing) and the in-use set only ever runs
    /// <c>Add</c>/<c>Remove</c> on a hash table that is already sized.
    ///
    /// An exhausted pool is a warm-up planning error, not a normal condition, so it
    /// is warned about and then served by allocating one block. The alternative —
    /// returning null — would make a unit invisible, and a warning is far easier to
    /// trace back to the load screen than a unit that quietly never appears. The
    /// <see cref="MaximumViews"/> ceiling is where even that stops, because
    /// unbounded growth across a long match is the leak this class exists to prevent.
    /// </summary>
    public sealed class UnitViewPool : IDisposable
    {
        /// <summary>Views added at once when an exhausted pool has to grow.</summary>
        public const int DefaultGrowBlock = 32;

        /// <summary>
        /// Hard ceiling on pooled views, mirroring the replication table: one view
        /// per unit this client can hold at all.
        /// </summary>
        public const int DefaultMaximumViews = ClientReplicationWorld.DefaultCapacity;

        private readonly Transform _root;
        private readonly bool _ownsRoot;
        private readonly GameObject[] _prototypeByKind = new GameObject[UnitKinds.Count];
        private readonly Stack<UnitView>[] _freeByKind;
        private readonly int[] _activeByKind = new int[UnitKinds.Count];
        private readonly HashSet<UnitView> _inUse = new HashSet<UnitView>();
        private readonly List<UnitView> _created = new List<UnitView>();
        private readonly int _growBlock;
        private readonly int _maximumViews;

        private int _inUseCount;
        private long _growCount;
        private long _rejectedCount;
        private bool _disposed;

        /// <param name="root">
        /// Transform every view is parented to. One level keeps the hierarchy
        /// readable, and because <see cref="UnitView.Apply"/> writes world position
        /// and world rotation it costs nothing as long as the root is not scaled.
        /// Null creates — and then owns and destroys — a root of its own.
        /// </param>
        /// <param name="growBlock">Views added per exhaustion event.</param>
        /// <param name="maximumViews">Ceiling on views this pool may ever hold.</param>
        public UnitViewPool(
            Transform root = null,
            int growBlock = DefaultGrowBlock,
            int maximumViews = DefaultMaximumViews)
        {
            if (growBlock <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(growBlock));
            }

            if (maximumViews <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumViews));
            }

            if (root == null)
            {
                var owned = new GameObject("Unit Views");
                root = owned.transform;
                _ownsRoot = true;
            }

            _root = root;
            _growBlock = growBlock;
            _maximumViews = maximumViews;

            _freeByKind = new Stack<UnitView>[UnitKinds.Count];
            for (var kind = 0; kind < _freeByKind.Length; kind++)
            {
                _freeByKind[kind] = new Stack<UnitView>();
            }
        }

        /// <summary>Transform every pooled view lives under.</summary>
        public Transform Root => _root;

        /// <summary>Views handed out and not yet released.</summary>
        public int InUseCount => _inUseCount;

        /// <summary>Views this pool has created, free ones included.</summary>
        public int TotalCreated => _created.Count;

        /// <summary>Ceiling passed to the constructor.</summary>
        public int MaximumViews => _maximumViews;

        /// <summary>Times the pool had to grow past its warm-up (0 in a tuned match).</summary>
        public long GrowCount => _growCount;

        /// <summary>Requests refused at the ceiling: units that could not be presented.</summary>
        public long RejectedCount => _rejectedCount;

        /// <summary>True once <see cref="Dispose"/> has run.</summary>
        public bool IsDisposed => _disposed;

        /// <summary>
        /// Registers the content prototype of one archetype. The pool instantiates
        /// that object instead of building a placeholder, which is how real
        /// silhouettes (hull mesh + turret child, OD-26) enter presentation without
        /// any gameplay code learning about them.
        ///
        /// Rejected unless the prototype carries a <see cref="UnitView"/>: a view the
        /// pool cannot type would surface as a null handed to the binder mid-match,
        /// far away from the asset that caused it.
        /// </summary>
        public bool SetPrototype(byte kind, GameObject prototype)
        {
            var index = KindIndex(kind);
            if (prototype == null)
            {
                _prototypeByKind[index] = null;
                return false;
            }

            if (prototype.GetComponent<UnitView>() == null)
            {
                Debug.LogError(
                    $"{nameof(UnitViewPool)}: prototype for unit kind {kind} has no {nameof(UnitView)} component and was ignored.",
                    prototype);
                return false;
            }

            _prototypeByKind[index] = prototype;
            return true;
        }

        /// <summary>
        /// Ensures the archetype owns at least <paramref name="count"/> pooled views,
        /// creating only the missing ones, and leaves everything deactivated. Call it
        /// on load for the match's expected peak (400 units per OD-28, split across
        /// archetypes), never from a frame.
        ///
        /// Grow-to rather than add-N: a level that warmed up twice would otherwise
        /// double its own allocation and hide the fact that the first number was
        /// wrong.
        /// </summary>
        /// <returns>Views created by this call.</returns>
        public int Warmup(byte kind, int count)
        {
            if (count <= 0)
            {
                return 0;
            }

            if (_disposed)
            {
                Debug.LogWarning($"{nameof(UnitViewPool)}: warm-up after {nameof(Dispose)} is ignored.");
                return 0;
            }

            var index = KindIndex(kind);
            var free = _freeByKind[index];
            var owned = free.Count + _activeByKind[index];
            var created = 0;
            while (owned < count && _created.Count < _maximumViews)
            {
                var view = CreateView(kind);
                if (view == null)
                {
                    break;
                }

                _created.Add(view);
                free.Push(view);
                owned++;
                created++;
            }

            return created;
        }

        /// <summary>
        /// Takes a free, activated, unbound view of the archetype, growing the pool
        /// (and warning) when none is left. Returns null only at the
        /// <see cref="MaximumViews"/> ceiling.
        /// </summary>
        public UnitView Acquire(byte kind)
        {
            TryAcquire(kind, out var view);
            return view;
        }

        /// <summary>
        /// <see cref="Acquire"/> with the failure made explicit. The binder needs the
        /// distinction, because "no view" must skip presentation for one unit rather
        /// than abort the frame loop.
        /// </summary>
        public bool TryAcquire(byte kind, out UnitView view)
        {
            view = null;
            if (_disposed)
            {
                return false;
            }

            var index = KindIndex(kind);
            var free = _freeByKind[index];
            while (true)
            {
                if (free.Count == 0)
                {
                    if (!Grow(index, kind))
                    {
                        return false;
                    }

                    continue;
                }

                // An object destroyed outside the pool (an editor reload, a stray
                // Destroy) leaves a fake-null hole in the stack. Dropping those here
                // keeps one stale reference from disabling the archetype for good.
                var candidate = free.Pop();
                if (candidate != null)
                {
                    view = candidate;
                    break;
                }
            }

            view.ViewObject.SetActive(true);
            _activeByKind[index]++;
            _inUse.Add(view);
            _inUseCount++;
            return true;
        }

        /// <summary>
        /// Returns a view to its archetype's free list and deactivates it.
        ///
        /// False means the view was not in use by this pool. Releasing twice would
        /// put the same object on a free list twice, and the two following
        /// <see cref="Acquire"/> calls would then drive two units with one transform;
        /// releasing a foreign view would leak it into this pool's hierarchy. The
        /// in-use set is what catches both, which is why it exists at all.
        /// </summary>
        public bool Release(UnitView view)
        {
            if (view == null)
            {
                return false;
            }

            if (!_inUse.Remove(view))
            {
                Debug.LogWarning(
                    view.IsPooledView
                        ? $"{nameof(UnitViewPool)}: ignored a repeated release of {view.name}."
                        : $"{nameof(UnitViewPool)}: ignored a release of {view.name}, which this pool never created.");
                return false;
            }

            var index = KindIndex(view.UnitKind);
            view.Release();
            view.ViewObject.SetActive(false);
            _activeByKind[index]--;
            _inUseCount--;
            _freeByKind[index].Push(view);
            return true;
        }

        /// <summary>Releases every live view; the created objects stay in the pool.</summary>
        /// <returns>Views returned to the free lists.</returns>
        public int ReleaseAll()
        {
            var released = 0;
            for (var i = 0; i < _created.Count; i++)
            {
                var view = _created[i];
                if (view != null && _inUse.Contains(view) && Release(view))
                {
                    released++;
                }
            }

            return released;
        }

        /// <summary>Free views of one archetype.</summary>
        public int FreeCount(byte kind) => _freeByKind[KindIndex(kind)].Count;

        /// <summary>Views of one archetype currently handed out.</summary>
        public int ActiveCount(byte kind) => _activeByKind[KindIndex(kind)];

        /// <summary>Views the pool owns for one archetype: free and in use together.</summary>
        public int OwnedCount(byte kind)
        {
            var index = KindIndex(kind);
            return _freeByKind[index].Count + _activeByKind[index];
        }

        /// <summary>
        /// Destroys every view the pool created, plus its own root when it made one.
        /// A caller that supplied the root keeps it: that object belongs to the
        /// scene, not to the pool.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _inUse.Clear();
            _inUseCount = 0;
            Array.Clear(_activeByKind, 0, _activeByKind.Length);

            for (var i = 0; i < _created.Count; i++)
            {
                var view = _created[i];
                if (view != null)
                {
                    DestroyObject(view.ViewObject);
                }
            }

            _created.Clear();
            for (var kind = 0; kind < _freeByKind.Length; kind++)
            {
                _freeByKind[kind].Clear();
            }

            if (_ownsRoot && _root != null)
            {
                DestroyObject(_root.gameObject);
            }
        }

        /// <summary>
        /// Allocates one block of views for an archetype that outgrew its warm-up.
        /// Returns false, and counts the rejection, at the ceiling.
        /// </summary>
        private bool Grow(int index, byte kind)
        {
            var room = _maximumViews - _created.Count;
            if (room <= 0)
            {
                // Once, not per request: an un-presentable unit asks again on every
                // frame of the match, and a per-frame error buries the console — and
                // the log line that actually mattered — under duplicates.
                // <see cref="RejectedCount"/> keeps the total for telemetry.
                if (_rejectedCount == 0)
                {
                    Debug.LogError(
                        $"{nameof(UnitViewPool)}: ceiling of {_maximumViews} views reached, unit kind {kind} cannot be presented. Raise the warm-up or look for leaked views.");
                }

                _rejectedCount++;
                return false;
            }

            var block = room < _growBlock ? room : _growBlock;
            Debug.LogWarning(
                $"{nameof(UnitViewPool)}: unit kind {kind} exhausted its warm-up; allocated a block of {block}. Warm up to the match peak so the steady state stays allocation-free.");
            _growCount++;

            for (var i = 0; i < block; i++)
            {
                var view = CreateView(kind);
                if (view == null)
                {
                    break;
                }

                _created.Add(view);
                _freeByKind[index].Push(view);
            }

            return _freeByKind[index].Count > 0;
        }

        /// <summary>
        /// Creates one deactivated, unbound view of the archetype: the registered
        /// content prototype when there is one, otherwise the placeholder hierarchy
        /// (hull transform + turret child) that keeps the binder and the overlays
        /// exercisable before any roster art exists.
        /// </summary>
        private UnitView CreateView(byte kind)
        {
            var prototype = _prototypeByKind[KindIndex(kind)];
            UnitView view;
            if (prototype != null)
            {
                var instance = UnityEngine.Object.Instantiate(prototype, _root);
                view = instance.GetComponent<UnitView>();
                if (view == null)
                {
                    DestroyObject(instance);
                    Debug.LogError(
                        $"{nameof(UnitViewPool)}: prototype for unit kind {kind} lost its {nameof(UnitView)} after registration.");
                    return null;
                }
            }
            else
            {
                view = CreatePlaceholderView();
            }

            view.AssignKind(kind);
            view.MarkPooledView();
            view.name = $"{view.name} {kind}";
            view.ViewObject.SetActive(false);
            return view;
        }

        private UnitView CreatePlaceholderView()
        {
            var hull = new GameObject(nameof(UnitView));
            hull.transform.SetParent(_root, false);

            var turret = new GameObject("Turret");
            turret.transform.SetParent(hull.transform, false);

            var view = hull.AddComponent<UnitView>();
            view.AssignTurret(turret.transform);
            return view;
        }

        /// <summary>
        /// An archetype outside <see cref="UnitKinds"/> falls back to
        /// <see cref="UnitKinds.Unknown"/> instead of throwing: a newer server build
        /// may add a kind this client's table predates, and one unrecognised
        /// archetype must not take the frame loop down with it.
        /// </summary>
        private static int KindIndex(byte kind) =>
            kind < UnitKinds.Count ? kind : UnitKinds.Unknown;

        private static void DestroyObject(GameObject target)
        {
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(target);
#else
            UnityEngine.Object.Destroy(target);
#endif
        }
    }
}
