using System.Collections.Generic;
using GlobalFront.Client;
using GlobalFront.Client.Presentation;
using GlobalFront.Server.Transport;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GlobalFront.Tests.EditMode.Client
{
    /// <summary>
    /// Phase 4, step 4.2.1: the client bootstrap's command-line surface.
    ///
    /// The parse is asserted against the exact shapes a launcher writes —
    /// space and equals separated, with and without a port — because the
    /// failure mode here is silent: a launcher that cannot be parsed boots
    /// the single-player prototype instead of connecting, which looks like a
    /// server problem rather than an argument problem.
    /// </summary>
    [TestFixture]
    public sealed class GlobalFrontRuntimeBootstrapTests
    {
        private readonly List<TacticalVerticalSliceRunner> _runners =
            new List<TacticalVerticalSliceRunner>();
        private readonly List<GameObject> _created = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _runners.Count; index++)
            {
                var runner = _runners[index];
                if (runner == null)
                {
                    continue;
                }

                // The bootstrap owns the session it created; the runner only
                // borrows it, so Shutdown alone would leave the carrier open.
                var session = runner.RemoteSession;
                runner.Shutdown();
                session?.Dispose();

                if (runner.gameObject != null)
                {
                    Object.DestroyImmediate(runner.gameObject);
                }
            }

            _runners.Clear();

            for (var index = 0; index < _created.Count; index++)
            {
                if (_created[index] != null)
                {
                    Object.DestroyImmediate(_created[index]);
                }
            }

            _created.Clear();
        }

        [Test]
        public void TryParseConnectEndpoint_WithSpace_ParsesHostAndPort()
        {
            var parsed = GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint(
                new[] { "-connect", "192.168.1.50:8888" },
                out var host,
                out var port);

            Assert.That(parsed, Is.True);
            Assert.That(host, Is.EqualTo("192.168.1.50"));
            Assert.That(port, Is.EqualTo(8888));
        }

        [Test]
        public void TryParseConnectEndpoint_WithEquals_ParsesHostAndPort()
        {
            var parsed = GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint(
                new[] { "-connect=10.0.0.1:9000" },
                out var host,
                out var port);

            Assert.That(parsed, Is.True);
            Assert.That(host, Is.EqualTo("10.0.0.1"));
            Assert.That(port, Is.EqualTo(9000));
        }

        [Test]
        public void TryParseConnectEndpoint_WithoutPort_DefaultsTo7777()
        {
            var parsed = GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint(
                new[] { "-connect", "127.0.0.1" },
                out var host,
                out var port);

            Assert.That(parsed, Is.True);
            Assert.That(host, Is.EqualTo("127.0.0.1"));
            Assert.That(port, Is.EqualTo(GlobalFrontRuntimeBootstrap.DefaultConnectPort));
            Assert.That(port, Is.EqualTo(7777));
        }

        [Test]
        public void TryParseConnectEndpoint_WithEqualsWithoutPort_DefaultsTo7777()
        {
            var parsed = GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint(
                new[] { "-connect=127.0.0.1" },
                out var host,
                out var port);

            Assert.That(parsed, Is.True);
            Assert.That(host, Is.EqualTo("127.0.0.1"));
            Assert.That(port, Is.EqualTo(7777));
        }

        [Test]
        public void TryParseConnectEndpoint_NoFlag_ReturnsFalse()
        {
            var parsed = GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint(
                new[] { "-someOtherFlag" },
                out var host,
                out var port);

            Assert.That(parsed, Is.False);
            Assert.That(host, Is.Null);
            Assert.That(port, Is.Zero);
        }

        [Test]
        public void TryParseConnectEndpoint_InvalidPort_ReturnsFalse()
        {
            var parsed = GlobalFrontRuntimeBootstrap.TryParseConnectEndpoint(
                new[] { "-connect", "127.0.0.1:999999" },
                out _,
                out _);

            Assert.That(parsed, Is.False);
        }

        [Test]
        public void ActivateDedicatedClient_BuildsRemoteSliceAndStartsConnecting()
        {
            var runner = GlobalFrontRuntimeBootstrap.ActivateDedicatedClient(
                "127.0.0.1",
                7777,
                createVisuals: false);
            _runners.Add(runner);

            Assert.That(runner.IsInitialized, Is.True);
            Assert.That(runner.NetworkMode, Is.EqualTo(SliceNetworkMode.DedicatedClient));
            Assert.That(runner.Host, Is.Null,
                "a dedicated client must not simulate a match of its own");
            Assert.That(runner.RemoteSession, Is.Not.Null);
            Assert.That(
                runner.RemoteSession.Endpoint.State,
                Is.EqualTo(ClientTransportState.Connecting),
                "Connect must open the handshake before the first frame is pumped");
            Assert.That(
                GameObject.Find(GlobalFrontRuntimeBootstrap.TacticalSliceRootName) ==
                runner.gameObject,
                Is.True,
                "the runner must live on the named slice root the bootstrap replaces");
        }

        [Test]
        public void ActivateDedicatedClient_RemovesLegacyPrototype()
        {
            var runtimeRoot = new GameObject("[GlobalFront Runtime]");
            _created.Add(runtimeRoot);
            runtimeRoot.AddComponent<PrototypeRtsController>();
            runtimeRoot.AddComponent<PrototypeHud>();
            var prototypeWorld = new GameObject("[GlobalFront Prototype World]");
            _created.Add(prototypeWorld);

            var runner = GlobalFrontRuntimeBootstrap.ActivateDedicatedClient(
                "127.0.0.1",
                7777,
                createVisuals: false);
            _runners.Add(runner);

            Assert.That(GameObject.Find("[GlobalFront Prototype World]"), Is.Null);
            Assert.That(Object.FindAnyObjectByType<PrototypeRtsController>(), Is.Null);
            Assert.That(Object.FindAnyObjectByType<PrototypeHud>(), Is.Null);
        }

        [Test]
        public void ActivateDedicatedClient_ReplacesPreviousSlice()
        {
            var first = GlobalFrontRuntimeBootstrap.ActivateDedicatedClient(
                "127.0.0.1",
                7777,
                createVisuals: false);
            _runners.Add(first);
            var firstRoot = first.gameObject;

            var second = GlobalFrontRuntimeBootstrap.ActivateDedicatedClient(
                "127.0.0.1",
                7777,
                createVisuals: false);
            _runners.Add(second);

            Assert.That(firstRoot == null, Is.True,
                "re-activation must destroy the previous client slice instead of stacking a second one");
            Assert.That(
                GameObject.Find(GlobalFrontRuntimeBootstrap.TacticalSliceRootName) ==
                second.gameObject,
                Is.True,
                "the surviving slice root is the one the bootstrap just created");
        }
    }
}
