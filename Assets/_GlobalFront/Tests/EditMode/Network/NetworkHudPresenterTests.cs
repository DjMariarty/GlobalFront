using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.UI;
using GlobalFront.Core.Combat;
using GlobalFront.Core.Model;
using GlobalFront.Core.Movement;
using GlobalFront.Core.Reconnect;
using GlobalFront.Server.Sessions;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;

namespace GlobalFront.Tests.EditMode.Network
{
    [TestFixture]
    public sealed class NetworkHudPresenterTests
    {
        private static readonly CombatStats TestStats = new CombatStats(
            maximumHealth: 100,
            damage: 25,
            rangeMm: 7000,
            cooldownTicks: 10);

        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private readonly List<NetworkHudPresenter> _presenters = new List<NetworkHudPresenter>();

        [TearDown]
        public void TearDown()
        {
            for (var i = 0; i < _presenters.Count; i++)
            {
                _presenters[i]?.Dispose();
            }
            _presenters.Clear();

            for (var i = 0; i < _createdObjects.Count; i++)
            {
                if (_createdObjects[i] != null)
                {
                    Object.DestroyImmediate(_createdObjects[i]);
                }
            }
            _createdObjects.Clear();
        }

        private TacticalPauseOverlay CreateOverlay()
        {
            var go = new GameObject("TacticalPauseOverlayTest");
            _createdObjects.Add(go);
            return go.AddComponent<TacticalPauseOverlay>();
        }

        private NetworkHudPresenter CreatePresenter(
            LocalMatchHost host,
            ClientReconnectCoordinator coordinator = null,
            TacticalPauseOverlay overlay = null)
        {
            var presenter = new NetworkHudPresenter(host, coordinator, overlay);
            _presenters.Add(presenter);
            return presenter;
        }

        [Test]
        public void Disconnect_ActivatesPauseOverlayAndDisplaysPausingPlayer()
        {
            var host = new LocalMatchHost();
            var overlay = CreateOverlay();
            var presenter = CreatePresenter(host, overlay: overlay);

            var match = host.CreateSessionMatch(4);
            var s0 = host.CreateSession(host.CreateConnectionHandle());
            var s1 = host.CreateSession(host.CreateConnectionHandle());
            var s2 = host.CreateSession(host.CreateConnectionHandle());
            var s3 = host.CreateSession(host.CreateConnectionHandle());

            host.TryJoinMatch(s0, match, out var p0);
            host.TryJoinMatch(s1, match, out var p1);
            host.TryJoinMatch(s2, match, out var p2);
            host.TryJoinMatch(s3, match, out var p3);

            var template = BuildFourPlayerTemplate(p0, p1, p2, p3);
            Assert.That(host.TryStartSessionMatch(match, template, out _), Is.True);

            // Initially no pause
            Assert.That(presenter.State.IsTacticalPauseActive, Is.False);
            Assert.That(overlay.IsVisible, Is.False);
            Assert.That(presenter.State.Slots[0].SlotStatus, Is.EqualTo(SlotStatus.Connected));

            // Disconnect Player 0
            Assert.That(host.NotifyConnectionLost(s0, host.CurrentTick), Is.True);

            // Verify presenter and overlay state
            Assert.That(presenter.State.IsTacticalPauseActive, Is.True, "Presenter must activate tactical pause");
            Assert.That(presenter.State.PausingPlayer, Is.EqualTo(p0), "Presenter must record pausing player");
            Assert.That(presenter.State.RemainingGraceSeconds, Is.EqualTo(200.0f).Within(0.01f), "Grace must start at 200s");
            Assert.That(presenter.State.Slots[0].SlotStatus, Is.EqualTo(SlotStatus.Disconnected), "Slot 0 must be Disconnected");

            Assert.That(overlay.IsVisible, Is.True, "Overlay must become visible");
            Assert.That(overlay.Message, Does.Contain($"Игрок {p0}"));
            Assert.That(overlay.Message, Is.EqualTo($"Тактическая пауза: Игрок {p0} переподключается..."));
            Assert.That(overlay.TimerText, Is.EqualTo("Осталось времени: 200с"));
        }

        [Test]
        public void GraceTimer_CountsDownAccuratelyDuringPause()
        {
            var host = new LocalMatchHost();
            var overlay = CreateOverlay();
            var presenter = CreatePresenter(host, overlay: overlay);

            var match = host.CreateSessionMatch(4);
            var s0 = host.CreateSession(host.CreateConnectionHandle());
            var s1 = host.CreateSession(host.CreateConnectionHandle());
            var s2 = host.CreateSession(host.CreateConnectionHandle());
            var s3 = host.CreateSession(host.CreateConnectionHandle());

            host.TryJoinMatch(s0, match, out var p0);
            host.TryJoinMatch(s1, match, out var p1);
            host.TryJoinMatch(s2, match, out var p2);
            host.TryJoinMatch(s3, match, out var p3);

            var template = BuildFourPlayerTemplate(p0, p1, p2, p3);
            host.TryStartSessionMatch(match, template, out _);

            host.NotifyConnectionLost(s0, host.CurrentTick);
            Assert.That(presenter.State.RemainingGraceSeconds, Is.EqualTo(200.0f).Within(0.01f));

            // 1. Advance real-time by 5.0 seconds
            host.AdvanceRealTime(5.0);
            Assert.That(presenter.State.RemainingGraceSeconds, Is.EqualTo(195.0f).Within(0.01f));
            Assert.That(overlay.TimerText, Is.EqualTo("Осталось времени: 195с"));

            // 2. Advance 40 pause ticks directly (40 * 0.05s = 2.0s)
            host.AdvancePauseTicks(40);
            Assert.That(presenter.State.RemainingGraceSeconds, Is.EqualTo(193.0f).Within(0.01f));
            Assert.That(overlay.TimerText, Is.EqualTo("Осталось времени: 193с"));

            // 3. Direct advance on presenter
            presenter.AdvanceRealTime(3.0);
            Assert.That(presenter.State.RemainingGraceSeconds, Is.EqualTo(190.0f).Within(0.01f));
            Assert.That(overlay.TimerText, Is.EqualTo("Осталось времени: 190с"));
        }

        [Test]
        public void ResumeCountdown_UpdatesSecondsAndClearsOnResume()
        {
            var host = new LocalMatchHost();
            var overlay = CreateOverlay();
            var coordinator = new ClientReconnectCoordinator();
            var presenter = CreatePresenter(host, coordinator, overlay);

            var match = host.CreateSessionMatch(4);
            var s0 = host.CreateSession(host.CreateConnectionHandle());
            host.TryJoinMatch(s0, match, out var p0);

            coordinator.CacheSession(s0, match, p0, default);
            coordinator.OnTransportDisconnected(TransportDisconnectReason.TransportLost);
            coordinator.OnReconnectResponse(new ReconnectResponse(
                GlobalFront.Core.Reconnect.ReconnectResult.Accepted,
                p0,
                match,
                100UL,
                1));

            // Installing keyframe triggers the 5-second countdown
            coordinator.OnKeyframeInstalled();

            Assert.That(presenter.State.IsResumeCountdownActive, Is.True, "Countdown must be active");
            Assert.That(presenter.State.CountdownSecondsRemaining, Is.EqualTo(5), "Countdown must start at 5");
            Assert.That(overlay.IsVisible, Is.True);
            Assert.That(overlay.CountdownBannerText, Is.EqualTo("Бой продолжится через: 5..."));

            // Advance countdown by 1s (4s remaining)
            coordinator.AdvanceCountdown(1.0f, out _);
            presenter.AdvanceRealTime(1.0);
            Assert.That(presenter.State.CountdownSecondsRemaining, Is.EqualTo(4));
            Assert.That(overlay.CountdownBannerText, Is.EqualTo("Бой продолжится через: 4..."));

            // Advance countdown to completion (4s elapsed) -> ReadyToResume fires -> host / presenter resume
            coordinator.AdvanceCountdown(4.0f, out var ready);
            Assert.That(ready, Is.True);

            // Simulation resumes: pause & countdown cleared, overlay hidden
            Assert.That(presenter.State.IsResumeCountdownActive, Is.False, "Countdown flag must be cleared");
            Assert.That(presenter.State.IsTacticalPauseActive, Is.False, "Tactical pause must be cleared");
            Assert.That(overlay.IsVisible, Is.False, "Overlay must be hidden");
        }

        [Test]
        public void PlayerAbandonment_UpdatesSlotStatusToAbandoned()
        {
            // Host with default 200s grace (4000 ticks)
            var host = new LocalMatchHost();
            var presenter = CreatePresenter(host);

            var match = host.CreateSessionMatch(4);
            var s0 = host.CreateSession(host.CreateConnectionHandle());
            var s1 = host.CreateSession(host.CreateConnectionHandle());
            var s2 = host.CreateSession(host.CreateConnectionHandle());
            var s3 = host.CreateSession(host.CreateConnectionHandle());

            host.TryJoinMatch(s0, match, out var p0);
            host.TryJoinMatch(s1, match, out var p1);
            host.TryJoinMatch(s2, match, out var p2);
            host.TryJoinMatch(s3, match, out var p3);

            var template = BuildFourPlayerTemplate(p0, p1, p2, p3);
            host.TryStartSessionMatch(match, template, out _);

            // Player 0 disconnects
            host.NotifyConnectionLost(s0, 0);
            Assert.That(presenter.State.Slots[0].SlotStatus, Is.EqualTo(SlotStatus.Disconnected));

            // Advance 4000 ticks: still in grace
            host.AdvancePauseTicks(4000);
            Assert.That(presenter.State.Slots[0].SlotStatus, Is.EqualTo(SlotStatus.Disconnected));

            // Advance 1 more tick past 4000 ticks (200.0s elapsed) -> GraceExpired -> Abandoned
            host.AdvancePauseTicks(1);

            Assert.That(presenter.State.Slots[0].SlotStatus, Is.EqualTo(SlotStatus.Abandoned),
                "Slot 0 status must transition to Abandoned once 200s grace window expires");
        }

        [Test]
        public void Overlay_HeadlessNullSafety()
        {
            var overlay = CreateOverlay();

            // None of the UI serializable fields are assigned (headless/test mode).
            // None of these methods should throw NullReferenceException.
            Assert.DoesNotThrow(() => overlay.SetVisible(true));
            Assert.That(overlay.IsVisible, Is.True);

            Assert.DoesNotThrow(() => overlay.UpdatePauseState(new PlayerId(2), 150.3f));
            Assert.That(overlay.Message, Is.EqualTo("Тактическая пауза: Игрок 2 переподключается..."));
            Assert.That(overlay.TimerText, Is.EqualTo("Осталось времени: 150с"));

            Assert.DoesNotThrow(() => overlay.UpdateCountdown(3));
            Assert.That(overlay.CountdownBannerText, Is.EqualTo("Бой продолжится через: 3..."));

            Assert.DoesNotThrow(() => overlay.Hide());
            Assert.That(overlay.IsVisible, Is.False);
        }

        [Test]
        public void Topology2v2_SlotMapping_IsCorrect()
        {
            var state = new NetworkMatchHudState();

            Assert.That(state.Slots.Length, Is.EqualTo(4));

            Assert.That(state.Slots[0].PlayerId, Is.EqualTo(new PlayerId(1)));
            Assert.That(state.Slots[0].TeamId, Is.EqualTo(MatchTopology2v2.TeamRed));

            Assert.That(state.Slots[1].PlayerId, Is.EqualTo(new PlayerId(2)));
            Assert.That(state.Slots[1].TeamId, Is.EqualTo(MatchTopology2v2.TeamRed));

            Assert.That(state.Slots[2].PlayerId, Is.EqualTo(new PlayerId(3)));
            Assert.That(state.Slots[2].TeamId, Is.EqualTo(MatchTopology2v2.TeamBlue));

            Assert.That(state.Slots[3].PlayerId, Is.EqualTo(new PlayerId(4)));
            Assert.That(state.Slots[3].TeamId, Is.EqualTo(MatchTopology2v2.TeamBlue));
        }

        private static MatchConfig BuildFourPlayerTemplate(PlayerId p0, PlayerId p1, PlayerId p2, PlayerId p3)
        {
            var specs = new[]
            {
                new UnitSpawnSpec(p0, new WorldPointMm(10000, 10000), 500, false),
                new UnitSpawnSpec(p1, new WorldPointMm(15000, 10000), 500, false),
                new UnitSpawnSpec(p2, new WorldPointMm(50000, 50000), 500, false),
                new UnitSpawnSpec(p3, new WorldPointMm(55000, 50000), 500, false)
            };
            return new MatchConfig(TestStats, specs);
        }
    }
}
