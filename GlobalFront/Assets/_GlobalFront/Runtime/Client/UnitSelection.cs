using System.Collections.Generic;
using GlobalFront.Core.Model;
using UnityEngine;
using CoreEntityId = GlobalFront.Core.Model.EntityId;

namespace GlobalFront.Client
{
    /// <summary>
    /// Owns the player's unit selection: click selection, drag-box
    /// selection, Shift add/remove toggling, clearing and pruning of
    /// destroyed units. Selection is always sourced from
    /// <see cref="UnitRegistry"/>; no unit data is duplicated.
    /// Presentation-side picking (raycasts) is delegated here by the
    /// input reader so that the controller remains a facade.
    /// </summary>
    public sealed class UnitSelection
    {
        private readonly UnitRegistry _registry;
        private readonly List<PrototypeUnit> _selectedUnits =
            new List<PrototypeUnit>();

        public UnitSelection(UnitRegistry registry)
        {
            _registry = registry;
        }

        public int Count => _selectedUnits.Count;

        public CoreEntityId[] GetSelectedEntityIds()
        {
            var entities = new CoreEntityId[_selectedUnits.Count];
            for (var index = 0; index < _selectedUnits.Count; index++)
            {
                entities[index] = _selectedUnits[index].Entity;
            }

            return entities;
        }

        /// <summary>
        /// Applies a single-pointer pick. When <paramref name="additive"/>
        /// is false the previous selection is cleared. When the picked unit
        /// is already selected in additive mode it is deselected instead.
        /// </summary>
        public void SelectUnderPointer(
            Camera camera,
            Vector2 pointer,
            bool additive,
            PlayerId localPlayer)
        {
            var ray = camera.ScreenPointToRay(pointer);
            PrototypeUnit hitUnit = null;

            if (Physics.Raycast(
                    ray,
                    out var hit,
                    600f,
                    Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                hitUnit = hit.collider.GetComponentInParent<PrototypeUnit>();
                if (hitUnit != null &&
                    (!hitUnit.IsAlive || hitUnit.Owner != localPlayer))
                {
                    hitUnit = null;
                }
            }

            if (!additive)
            {
                Clear();
            }

            if (hitUnit == null)
            {
                return;
            }

            if (additive && hitUnit.IsSelected)
            {
                SetUnitSelected(hitUnit, false);
            }
            else
            {
                SetUnitSelected(hitUnit, true);
            }

            Sort();
        }

        /// <summary>
        /// Applies a drag-box selection in screen coordinates. When
        /// <paramref name="additive"/> is false the previous selection is
        /// cleared. Only alive units owned by <paramref name="localPlayer"/>
        /// whose screen projection falls inside the rect are selected.
        /// </summary>
        public void SelectInsideScreenRect(
            Camera camera,
            Vector2 start,
            Vector2 end,
            bool additive,
            PlayerId localPlayer)
        {
            if (!additive)
            {
                Clear();
            }

            var minimum = Vector2.Min(start, end);
            var maximum = Vector2.Max(start, end);

            for (var index = 0; index < _registry.Count; index++)
            {
                var unit = _registry[index];
                if (!unit.IsAlive || unit.Owner != localPlayer)
                {
                    continue;
                }

                var screenPoint = camera.WorldToScreenPoint(unit.transform.position);
                if (screenPoint.z <= 0f)
                {
                    continue;
                }

                if (screenPoint.x >= minimum.x && screenPoint.x <= maximum.x &&
                    screenPoint.y >= minimum.y && screenPoint.y <= maximum.y)
                {
                    SetUnitSelected(unit, true);
                }
            }

            Sort();
        }

        public void Clear()
        {
            for (var index = 0; index < _selectedUnits.Count; index++)
            {
                _selectedUnits[index].SetSelected(false);
            }

            _selectedUnits.Clear();
        }

        /// <summary>
        /// Removes destroyed units from the selection and clears their
        /// visual selected flag. Call once per simulation tick after
        /// combat resolution.
        /// </summary>
        public void PruneDead()
        {
            for (var index = _selectedUnits.Count - 1; index >= 0; index--)
            {
                if (_selectedUnits[index].IsAlive)
                {
                    continue;
                }

                _selectedUnits[index].SetSelected(false);
                _selectedUnits.RemoveAt(index);
            }
        }

        private void SetUnitSelected(PrototypeUnit unit, bool selected)
        {
            if (!unit.IsAlive || unit.IsSelected == selected)
            {
                return;
            }

            unit.SetSelected(selected);
            if (selected)
            {
                _selectedUnits.Add(unit);
            }
            else
            {
                _selectedUnits.Remove(unit);
            }
        }

        private void Sort() => _selectedUnits.Sort(UnitRegistry.CompareUnits);
    }
}