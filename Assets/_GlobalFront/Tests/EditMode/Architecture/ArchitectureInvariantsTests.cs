using System;
using System.Collections.Generic;
using System.Reflection;
using GlobalFront.Client.Presentation;
using GlobalFront.Core.Movement;
using GlobalFront.Server;
using NUnit.Framework;
using UnityEngine;

namespace GlobalFront.Tests.EditMode.Architecture
{
    /// <summary>
    /// The architectural boundaries this project keeps by convention, stated as
    /// assertions so a later round cannot cross one by accident (step 3.7, part 2).
    ///
    /// Everything here is reflection over the assemblies Unity actually built, which is
    /// what makes it fast enough for every EditMode pass and honest about what shipped
    /// rather than about what the source tree looks like. Where a claim only means
    /// something in compiled form — "nothing calls <c>System.Linq</c>" — the method
    /// bodies are decoded and their metadata tokens resolved, because a using-directive
    /// scan is satisfied by an assembly that never references the type and defeated by
    /// one that spells it out inline.
    ///
    /// The invariants, and what each protects:
    ///
    /// * <c>GlobalFront.Core</c> references no engine and no layer above it, so the
    ///   simulation can be replayed and unit-tested outside Unity, and the server and the
    ///   client cannot disagree about a rule because one of them reached for <c>Mathf</c>.
    /// * <c>GlobalFront.Server</c> references no engine and never the client, so the
    ///   authoritative process ships as a dedicated server.
    /// * Authoritative state is integer-only. Whole millimetres and whole hit points are
    ///   why a re-simulated match reproduces byte for byte; one <c>float</c> in a unit
    ///   record turns the state checksum into a coin flip. The wall-clock accumulators that
    ///   pace the tick and the transport are exempt, and the exemption is pinned to the
    ///   fields that actually exist so it cannot widen.
    /// * Nothing calls <c>System.Linq</c>: an enumerator allocates, and allocation in the
    ///   tick or the frame loop is the OD-28 budget gone.
    /// * The deterministic layers draw no unseeded number and read no wall clock, so two
    ///   replays of one command log cannot diverge. Minting a session token is the one
    ///   place cryptography appears, and it is held to a stricter list still.
    /// * Every component in the client is <c>[DisallowMultipleComponent]</c>, because these
    ///   register with a shared pool, a shared pointer-blocker table and one camera, and a
    ///   second copy of any of them double-steps the first.
    /// </summary>
    [TestFixture]
    public sealed class ArchitectureInvariantsTests
    {
        private const string CoreAssemblyName = "GlobalFront.Core";
        private const string ServerAssemblyName = "GlobalFront.Server";
        private const string ClientAssemblyName = "GlobalFront.Client";

        /// <summary>Assembly-name prefixes that would put engine code below the simulation.</summary>
        private static readonly string[] EnginePrefixes = { "UnityEngine", "UnityEditor" };

        /// <summary>
        /// Types whose <c>double</c> fields are wall-clock bookkeeping rather than simulated
        /// state, and which therefore carry no authority over a replay. Named by full type
        /// name rather than by <c>typeof</c> so the gate does not depend on a transport
        /// helper's visibility, with an assertion below that each name still resolves.
        /// </summary>
        private static readonly string[] WallClockExemptions =
        {
            "GlobalFront.Core.Simulation.FixedStepClock",
            "GlobalFront.Server.TickDriver",
            "GlobalFront.Server.Transport.RateLimiter",
            "GlobalFront.Server.Transport.ImpairmentProfile",
        };

        /// <summary>
        /// The in-memory transport pipe. Impairment probabilities have to come from
        /// somewhere, and it draws them from a <see cref="Random"/> the profile seeds, so a
        /// lost packet stays reproducible. The exemption is that draw and nothing wider: a
        /// wall clock read in here would be a different bug, and stays a finding.
        /// </summary>
        private static readonly string[] TransportRandomExemption =
        {
            "GlobalFront.Server.Transport.VirtualNetworkPipe",
        };

        /// <summary>
        /// Namespaces that decide authoritative state, plus <c>MatchServer</c> itself. Neither
        /// may be timed, seeded or handed an identity: a match id is minted by the session
        /// layer and passed down, never produced where the simulation is looking.
        /// <c>Core.Model</c> and <c>Core.Simulation</c> are in the sweep because they hold the
        /// identifier types and the tick clock the simulation is built on.
        /// </summary>
        private static readonly string[] AuthoritativeNamespaces =
        {
            "GlobalFront.Core.Combat",
            "GlobalFront.Core.Model",
            "GlobalFront.Core.Movement",
            "GlobalFront.Core.Commands",
            "GlobalFront.Core.Simulation",
            "GlobalFront.Core.Snapshot",
        };

        /// <summary>Types inside those namespaces that are additionally named in the gate.</summary>
        private const string AuthoritativeServerType = ServerAssemblyName + ".MatchServer";

        /// <summary>
        /// Identity and wall-clock plumbing that is legal in the transport and session
        /// layers and illegal in the simulation.
        /// </summary>
        private static readonly string[] IdentityAndClockTypes =
        {
            "System.Security.Cryptography.RandomNumberGenerator",
            "System.Diagnostics.Stopwatch",
            "System.DateTime",
            "System.DateTimeOffset",
            "System.Random",
            "System.Environment",
        };

        /// <summary>
        /// <c>System.Guid</c> is the representation <c>SessionId</c> and <c>MatchId</c> carry
        /// (ADR-008), so storing, decoding and comparing one is not a nondeterminism. Naming
        /// the one operation that mints entropy is what lets the identifier types sit inside
        /// the authoritative sweep without the gate turning into a ban on the id format.
        /// </summary>
        private static readonly string[] EntropyMintingMembers =
        {
            "System.Guid.NewGuid",
        };

        /// <summary>
        /// Pre-Phase-3 prototype components: scene objects a designer may stack, and which
        /// nothing here shares state with. Nothing newer joins this list — the test that
        /// reads it fails when an exempted component grows the attribute anyway.
        /// </summary>
        private static readonly string[] LegacyPrototypeComponentExemptions =
        {
            "PrototypeHud",
            "PrototypeWorldBootstrap",
        };

        /// <summary>
        /// The exemptions that hold a floating-point field today. A new one appears the moment
        /// somebody adds a <c>double</c> to a type that has none, which is the drift this
        /// assertion exists to catch: an exemption list is only as narrow as the fields it
        /// excuses.
        /// </summary>
        private static readonly string[] ExemptionsCarryingFloatingPointState =
        {
            "GlobalFront.Core.Simulation.FixedStepClock",
            "GlobalFront.Server.Transport.RateLimiter",
            "GlobalFront.Server.Transport.ImpairmentProfile",
        };

        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private const BindingFlags AllMembers =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        private static Assembly CoreAssembly => typeof(WorldPointMm).Assembly;

        private static Assembly ServerAssembly => typeof(MatchServer).Assembly;

        private static Assembly ClientAssembly => typeof(UnitView).Assembly;

        // ----------------------------------------------------------------
        // 1 and 2 — layering
        // ----------------------------------------------------------------

        [Test]
        public void CoreAssembly_HasZeroReferencesToUnityOrServerOrClient()
        {
            Assert.That(CoreAssembly.GetName().Name, Is.EqualTo(CoreAssemblyName),
                "typeof(WorldPointMm) no longer resolves to the core assembly, so this gate " +
                "would be reading something else");

            var references = ReferencedAssemblyNames(CoreAssembly);
            var offenders = ForbiddenReferences(
                references,
                EnginePrefixes,
                new[] { ServerAssemblyName, ClientAssemblyName });

            Assert.That(offenders, Is.Empty,
                "the pure simulation layer must not depend on the engine or on anything above " +
                $"it, or it stops being replayable outside Unity; {Describe(offenders)} in " +
                $"{CoreAssemblyName} -> {Describe(references)}");
        }

        [Test]
        public void ServerAssembly_HasZeroReferencesToUnityOrClient()
        {
            Assert.That(ServerAssembly.GetName().Name, Is.EqualTo(ServerAssemblyName),
                "typeof(MatchServer) no longer resolves to the authoritative assembly");

            var references = ReferencedAssemblyNames(ServerAssembly);

            Assert.That(references, Contains.Item(CoreAssemblyName),
                "the server is expected to depend on the shared simulation core; if it no " +
                "longer does, the scan below proves nothing");

            var offenders = ForbiddenReferences(references, EnginePrefixes, new[] { ClientAssemblyName });

            Assert.That(offenders, Is.Empty,
                "the authoritative process must run as a dedicated server with no engine and no " +
                $"client code in it; {Describe(offenders)} in {ServerAssemblyName} -> " +
                $"{Describe(references)}");
        }

        /// <summary>
        /// The control for the two gates above: the same scan pointed at an assembly that
        /// really does reference the engine and both lower layers. Without it, "no offenders"
        /// is indistinguishable from a scan that cannot see anything.
        /// </summary>
        [Test]
        public void ReferenceScan_SeesTheDependenciesAnAssemblyReallyHas()
        {
            var references = ReferencedAssemblyNames(ClientAssembly);

            Assert.That(references, Contains.Item(CoreAssemblyName));
            Assert.That(references, Contains.Item(ServerAssemblyName));
            Assert.That(references, Has.Some.StartsWith("UnityEngine"),
                "the client does reference the engine, so a clean answer from the core and " +
                "server assemblies is a finding rather than a blind spot");
        }

        // ----------------------------------------------------------------
        // 3 — integer-only authoritative state
        // ----------------------------------------------------------------

        [Test]
        public void AuthoritativeSimulationAndSnapshotTypes_ContainZeroFloatingPointInstanceFields()
        {
            var offenders = new List<string>();
            var exemptTypesCarryingFloatingPoint = new List<string>();
            var exemptTypesWithoutFloatingPoint = new List<string>();
            var scanned = 0;

            var assemblies = new[] { CoreAssembly, ServerAssembly };
            for (var assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                var types = DefinedTypes(assemblies[assemblyIndex]);

                for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    var type = types[typeIndex];
                    var fields = FloatingPointFields(type);
                    var exempt = ContainsName(WallClockExemptions, type.FullName);

                    if (exempt)
                    {
                        (fields.Count > 0 ? exemptTypesCarryingFloatingPoint
                            : exemptTypesWithoutFloatingPoint).Add(type.FullName);
                        continue;
                    }

                    scanned++;

                    if (fields.Count > 0)
                    {
                        offenders.Add($"{type.FullName} carries {Describe(fields)}");
                    }
                }
            }

            Assert.That(scanned, Is.GreaterThan(60),
                $"the sweep covered only {scanned} types, which is far fewer than the two " +
                "simulation assemblies define");

            Assert.That(offenders, Is.Empty,
                "authoritative state is fixed-point: whole millimetres and whole hit points are " +
                "what makes a replay and a state checksum agree. A floating-point field in one " +
                $"of these types diverges the simulation; {Describe(offenders)}");

            Assert.That(exemptTypesCarryingFloatingPoint, Is.EquivalentTo(ExemptionsCarryingFloatingPointState),
                "the wall-clock exemption list and the fields it excuses have drifted apart: " +
                $"{Describe(exemptTypesCarryingFloatingPoint)}");

            // TickDriver is on the exemption list and holds no floating-point field of its
            // own: it exposes double properties that read FixedStepClock. Naming it here keeps
            // that from being mistaken for a hole in the gate.
            Assert.That(exemptTypesWithoutFloatingPoint, Is.EquivalentTo(new[]
            {
                "GlobalFront.Server.TickDriver",
            }), "an exemption that excuses no field should be deleted, not left to grow into " +
                $"one that excuses a real one; {Describe(exemptTypesWithoutFloatingPoint)}");

            for (var index = 0; index < WallClockExemptions.Length; index++)
            {
                Assert.That(
                    FindType(ServerAssembly, WallClockExemptions[index]) ??
                    FindType(CoreAssembly, WallClockExemptions[index]),
                    Is.Not.Null,
                    $"{WallClockExemptions[index]} is on the exemption list but no longer " +
                    "exists, so the list names a type the assemblies do not have");
            }
        }

        // ----------------------------------------------------------------
        // 4 — no LINQ in a runtime assembly
        // ----------------------------------------------------------------

        [Test]
        public void RuntimeAssemblies_NeverReferenceOrCallSystemLinq()
        {
            var offenders = new List<string>();
            var scannedTypes = 0;
            var decodedBodies = 0;
            var resolvedMembers = 0;

            var assemblies = new[] { CoreAssembly, ServerAssembly, ClientAssembly };
            for (var index = 0; index < assemblies.Length; index++)
            {
                var types = DefinedTypes(assemblies[index]);
                var cache = new TokenCache();
                scannedTypes += types.Length;

                for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    var type = types[typeIndex];

                    offenders.AddRange(BannedTypesInSignatures(
                        type, IsSystemLinqType, "System.Linq type in a signature"));

                    DecodeBodies(type, member => IsSystemLinqType(TargetTypeOf(member)),
                        offenders, cache, "System.Linq call", out var bodies);
                    decodedBodies += bodies;
                }

                resolvedMembers += cache.ResolvedMemberCount;
            }

            Assert.That(scannedTypes, Is.GreaterThan(100), "the sweep must cover the runtime");
            Assert.That(decodedBodies, Is.GreaterThan(400),
                $"the IL sweep decoded {decodedBodies} method bodies across the three runtime " +
                "assemblies, which is too few for any of them to have been read");
            Assert.That(resolvedMembers, Is.GreaterThan(500),
                $"the decoder resolved {resolvedMembers} distinct member tokens, which is far " +
                "below what these assemblies emit: a sweep that sees no tokens reports a clean " +
                "runtime whatever the code does");

            Assert.That(offenders, Is.Empty,
                "System.Linq allocates an enumerator per call, and an allocation inside the tick " +
                $"or the frame loop is the OD-28 budget gone; {Describe(offenders)}");
        }

        /// <summary>
        /// The control the LINQ gate cannot carry inside itself: a call site that really is
        /// there, in this assembly, which the same sweep has to find. Without it "no
        /// <c>System.Linq</c> anywhere in the runtime" is only as trustworthy as a decoder
        /// nobody ever pointed at code that would have tripped it.
        /// </summary>
        [Test]
        public void LinqScan_DetectsACallSiteThatReallyExists()
        {
            Assert.That(LinqCallSiteControl(), Is.EqualTo(3));

            var offenders = new List<string>();
            DecodeBodies(
                typeof(ArchitectureInvariantsTests),
                member => IsSystemLinqType(TargetTypeOf(member)),
                offenders,
                new TokenCache(),
                "System.Linq call",
                out var decodedBodies);

            Assert.That(decodedBodies, Is.GreaterThan(0),
                "this fixture has method bodies, so a sweep that decoded none is reading nothing");
            Assert.That(offenders, Has.Some.Contains(nameof(LinqCallSiteControl)),
                "the IL sweep did not see a LINQ call that is compiled into this very type: " +
                Describe(offenders));
        }

        private static int LinqCallSiteControl()
        {
            return System.Linq.Enumerable.Count(new int[3]);
        }

        /// <summary>
        /// The control the determinism gate cannot carry inside itself either: an unseeded
        /// construction and a <c>typeof</c> of the same type, both compiled into this fixture,
        /// which the same sweep has to name by the method that reaches for it. The second is
        /// the one that used to slip past — a type token resolves to a <see cref="System.Type"/>
        /// and a top-level type has no declaring type to read, so a predicate that looked only
        /// there watched a banned type arrive without a call and reported nothing.
        /// </summary>
        [Test]
        public void NondeterminismScan_DetectsACallSiteThatReallyExists()
        {
            Assert.That(RandomCallSiteControl(), Is.EqualTo(1),
                "a seeded draw is reproducible, which is the whole point of the exemption");
            Assert.That(RandomTypeTokenControl(), Is.EqualTo(typeof(System.Random)));

            var offenders = new List<string>();
            DecodeBodies(
                typeof(ArchitectureInvariantsTests),
                member => IsNondeterministicType(TargetTypeOf(member)),
                offenders,
                new TokenCache(),
                "nondeterministic call",
                out var decodedBodies);

            Assert.That(decodedBodies, Is.GreaterThan(0),
                "this fixture has method bodies, so a sweep that decoded none is reading nothing");
            Assert.That(offenders, Has.Some.Contains(nameof(RandomCallSiteControl)),
                "the IL sweep did not see a System.Random construction compiled into this very " +
                $"type: {Describe(offenders)}");
            Assert.That(offenders, Has.Some.Contains(nameof(RandomTypeTokenControl)),
                "the IL sweep did not see a typeof(System.Random) token, so a banned type that " +
                $"arrives without being called is still invisible to the gate: {Describe(offenders)}");
        }

        private static int RandomCallSiteControl()
        {
            return new System.Random(1984).Next(1, 2);
        }

        private static Type RandomTypeTokenControl() => typeof(System.Random);

        /// <summary>
        /// The transport exemption is the seeded draw, not the class that makes it. Pointed at
        /// a type that draws a seeded number *and* reads a clock, the narrowed predicate has to
        /// drop the draw and keep the clock; the wide exemption it replaced excused a wall
        /// clock inside the pipe, which is the thing the gate exists to catch.
        /// </summary>
        [Test]
        public void TransportExemption_ExcusesTheSeededDrawAndNothingElse()
        {
            var standIn = new TransportPipeStandIn();
            Assert.That(standIn.Draw(), Is.EqualTo(1));
            Assert.That(standIn.Now(), Is.GreaterThan(0L));

            var excused = new List<string>();
            DecodeBodies(
                typeof(TransportPipeStandIn),
                member => NondeterminismPredicate(true)(TargetTypeOf(member)),
                excused,
                new TokenCache(),
                "nondeterministic call",
                out _);

            Assert.That(excused, Has.None.Contains(nameof(TransportPipeStandIn.Draw)),
                "the seeded impairment draw is the exemption's whole justification: " +
                Describe(excused));
            Assert.That(excused, Has.Some.Contains(nameof(TransportPipeStandIn.Now)),
                "the exemption excused a wall clock as well, so a type-level pardon was still " +
                $"in force: {Describe(excused)}");

            var strict = new List<string>();
            DecodeBodies(
                typeof(TransportPipeStandIn),
                member => NondeterminismPredicate(false)(TargetTypeOf(member)),
                strict,
                new TokenCache(),
                "nondeterministic call",
                out _);
            Assert.That(strict, Has.Some.Contains(nameof(TransportPipeStandIn.Draw)),
                "the sweep cannot see the draw at all, so the assertion above proves nothing " +
                $"about the pardon: {Describe(strict)}");
        }

        private sealed class TransportPipeStandIn
        {
            public int Draw() => new System.Random(7).Next(1, 2);

            public long Now() => System.DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// <c>System.Guid</c> is the representation the session and match identifiers carry
        /// (ADR-008), so the authoritative gate has to tell a stored id from a minted one.
        /// These are the two sites that prove it does.
        /// </summary>
        [Test]
        public void AuthoritativeScan_ExcusesTheGuidCarrierAndFlagsAMint()
        {
            Assert.That(GuidCarrierControl(), Is.EqualTo(16));
            Assert.That(GuidMintControl() == default(System.Guid), Is.False);

            var offenders = new List<string>();
            DecodeBodies(
                typeof(ArchitectureInvariantsTests),
                IsIdentityOrClockTarget,
                offenders,
                new TokenCache(),
                "identity or wall-clock call",
                out _);

            Assert.That(offenders, Has.Some.Contains(nameof(GuidMintControl)),
                "an identifier minted where the simulation is looking is exactly the drift this " +
                $"gate exists for, and the sweep missed it: {Describe(offenders)}");
            Assert.That(offenders, Has.None.Contains(nameof(GuidCarrierControl)),
                "the identifier types decode a Guid by design; a gate that flags that has banned " +
                $"the id format rather than the entropy: {Describe(offenders)}");
        }

        private static int GuidCarrierControl() => new System.Guid(new byte[16]).ToByteArray().Length;

        private static System.Guid GuidMintControl() => System.Guid.NewGuid();

        // ----------------------------------------------------------------
        // 5 — determinism
        // ----------------------------------------------------------------

        [Test]
        public void DeterministicCoreAndServerSimulation_NeverUseSystemRandomOrDateTimeNow()
        {
            var offenders = new List<string>();
            var excused = new List<string>();
            var scannedTypes = 0;

            var assemblies = new[] { CoreAssembly, ServerAssembly };
            for (var assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                var types = DefinedTypes(assemblies[assemblyIndex]);
                var cache = new TokenCache();

                for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    var type = types[typeIndex];
                    var exempt = ContainsName(TransportRandomExemption, type.FullName);
                    if (!exempt)
                    {
                        scannedTypes++;
                    }

                    var pardoned = new List<string>();
                    var banned = NondeterminismPredicate(exempt, pardoned);
                    offenders.AddRange(BannedTypesInSignatures(
                        type, banned, "nondeterministic type in a signature"));
                    DecodeBodies(type, member => banned(TargetTypeOf(member)),
                        offenders, cache, "nondeterministic call", out _);

                    // What the exemption excuses is recorded by the predicate rather than
                    // inferred from an empty offender list: an exemption that excuses nothing
                    // is invisible either way, and that is the question this assertion asks.
                    if (pardoned.Count > 0)
                    {
                        excused.Add(type.FullName);
                    }
                }
            }

            Assert.That(scannedTypes, Is.GreaterThan(60), "the sweep must cover the simulation");

            Assert.That(excused, Is.EquivalentTo(TransportRandomExemption),
                "the seeded impairment pipe is the only place in the authoritative process " +
                "allowed an unseeded-looking RNG, and the exemption has to keep earning its " +
                $"place: {Describe(excused)}");

            Assert.That(offenders, Is.Empty,
                "nothing in the deterministic layers may draw a number it does not seed or read " +
                $"a wall clock, or two replays of one command log disagree; {Describe(offenders)}");
        }

        /// <summary>
        /// <c>Guid.NewGuid</c> and <c>RandomNumberGenerator</c> are legitimate where a session
        /// or a reconnection secret is minted, so they sit outside the gate above. Neither
        /// belongs where the simulation itself runs, and so the namespaces that decide
        /// authoritative state are held to a stricter list.
        /// </summary>
        [Test]
        public void AuthoritativeSimulationNamespaces_NeverUseGuidCryptoRandomOrStopwatch()
        {
            var offenders = new List<string>();
            var scannedTypes = 0;
            var swept = new List<string>();

            var assemblies = new[] { CoreAssembly, ServerAssembly };
            for (var assemblyIndex = 0; assemblyIndex < assemblies.Length; assemblyIndex++)
            {
                var types = DefinedTypes(assemblies[assemblyIndex]);
                var cache = new TokenCache();

                for (var typeIndex = 0; typeIndex < types.Length; typeIndex++)
                {
                    var type = types[typeIndex];
                    if (!IsAuthoritativeType(type))
                    {
                        continue;
                    }

                    scannedTypes++;
                    swept.Add(type.Name);

                    offenders.AddRange(BannedTypesInSignatures(
                        type,
                        candidate => ContainsName(IdentityAndClockTypes, candidate.FullName),
                        "identity or wall-clock type in a signature"));
                    DecodeBodies(
                        type,
                        IsIdentityOrClockTarget,
                        offenders,
                        cache,
                        "identity or wall-clock call",
                        out _);
                }
            }

            Assert.That(scannedTypes, Is.GreaterThan(10),
                "the authoritative namespaces must still be present in the compiled assemblies; " +
                "a rename would otherwise switch this gate off in silence");

            // Witnesses that the two namespaces holding the identifier types and the tick
            // clock are inside this sweep, rather than the gate matching a spelling nobody
            // uses any more and reporting a clean simulation.
            Assert.That(swept, Contains.Item("SessionId"),
                "Core.Model has to stay inside the authoritative sweep, or an id could start " +
                "minting its own entropy where the simulation is looking");
            Assert.That(swept, Contains.Item("FixedStepClock"),
                "Core.Simulation has to stay inside the authoritative sweep, or the clock the " +
                "tick counter is built from stops being read by this gate");

            Assert.That(offenders, Is.Empty,
                "match and entity ids are assigned by the session layer and handed down, and the " +
                $"simulation's clock is the tick counter, not a stopwatch; {Describe(offenders)}");
        }

        // ----------------------------------------------------------------
        // 6 — component arity
        // ----------------------------------------------------------------

        [Test]
        public void Phase3RuntimeMonoBehaviours_CarryDisallowMultipleComponent()
        {
            var components = new List<Type>();
            var offenders = new List<string>();

            var types = DefinedTypes(ClientAssembly);
            for (var index = 0; index < types.Length; index++)
            {
                var type = types[index];
                if (type.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    continue;
                }

                components.Add(type);

                if (ContainsName(LegacyPrototypeComponentExemptions, type.Name) ||
                    type.IsDefined(typeof(DisallowMultipleComponent), false))
                {
                    continue;
                }

                offenders.Add(type.FullName);
            }

            var names = TypeNames(components);

            // The brief points at GlobalFront.Client.Camera and GlobalFront.Client.Input, and
            // neither namespace exists: the rig and the input manager live in the root
            // GlobalFront.Client namespace in folders of those names. Sweeping the assembly is
            // what makes that difference moot, and these are the witnesses that the sweep
            // really does reach every folder it was meant to cover.
            Assert.That(names, Contains.Item("RtsCameraController"), "the camera folder is covered");
            Assert.That(names, Contains.Item("RtsInputManager"), "the input folder is covered");
            Assert.That(names, Contains.Item("UnitView"), "the presentation folder is covered");
            Assert.That(names, Contains.Item("MinimapInteractionController"), "the UI folder is covered");
            Assert.That(names, Contains.Item("TacticalVerticalSliceRunner"));
            Assert.That(names, Is.Not.Contains("UnitOverlayRendererFeature"),
                "a ScriptableRendererFeature is a ScriptableObject, not a component, and must " +
                "not be swept in by a base-type test that reads through the wrong parent");
            Assert.That(components.Count, Is.GreaterThanOrEqualTo(12),
                $"only {components.Count} components were swept, which is fewer than Phase 3 ships");

            Assert.That(offenders, Is.Empty,
                "each of these components registers with one shared pool, pointer-blocker table " +
                "or camera, so a second copy of any of them double-steps the first; " +
                Describe(offenders));

            for (var index = 0; index < LegacyPrototypeComponentExemptions.Length; index++)
            {
                var exempted = FindNamedType(components, LegacyPrototypeComponentExemptions[index]);
                Assert.That(exempted, Is.Not.Null,
                    $"{LegacyPrototypeComponentExemptions[index]} is gone from the client assembly");
                Assert.That(
                    exempted.IsDefined(typeof(DisallowMultipleComponent), false),
                    Is.False,
                    $"{LegacyPrototypeComponentExemptions[index]} now carries the attribute, so " +
                    "the exemption that excuses it is dead weight and the list stops marking a " +
                    "boundary");
            }
        }

        // ----------------------------------------------------------------
        // scanning
        // ----------------------------------------------------------------

        /// <summary>
        /// Resolves a metadata token at most once per assembly. Resolution is the only real
        /// cost in the IL sweep and most candidate windows are not member tokens at all, so
        /// failures are remembered too — but a remembered answer is still applied at every
        /// occurrence, which is what lets an exempt type excuse only itself.
        /// </summary>
        private sealed class TokenCache
        {
            private readonly Dictionary<int, MemberInfo> _members = new Dictionary<int, MemberInfo>();

            private readonly HashSet<int> _unresolvable = new HashSet<int>();

            /// <summary>
            /// How many distinct member tokens this sweep resolved. A decoder that resolved
            /// nothing would report no LINQ and no RNG in a codebase full of both, so every
            /// gate that reads IL states this number and asserts on it.
            /// </summary>
            public int ResolvedMemberCount => _members.Count;

            public MemberInfo Resolve(Module module, int token)
            {
                if (_members.TryGetValue(token, out var cached))
                {
                    return cached;
                }

                if (_unresolvable.Contains(token))
                {
                    return null;
                }

                try
                {
                    MemberInfo member = null;
                    if (IsTypeToken(token))
                    {
                        try
                        {
                            member = module.ResolveType(token, Type.EmptyTypes, Type.EmptyTypes);
                        }
                        catch (Exception)
                        {
                            // A TypeSpec for an open generic instantiation needs a context
                            // this sweep cannot supply. The member call below is tried
                            // anyway, and a refusal on both paths is recorded as unresolvable.
                            member = null;
                        }
                    }

                    member ??= module.ResolveMember(token, Type.EmptyTypes, Type.EmptyTypes);

                    if (member == null)
                    {
                        _unresolvable.Add(token);
                        return null;
                    }

                    _members.Add(token, member);
                    return member;
                }
                catch (Exception)
                {
                    // Either a coincidental four-byte window that spells a token the module
                    // has no row for, or a row needing a generic context this sweep cannot
                    // supply. A generic instantiation of a banned definition still arrives as
                    // the plain MemberRef for that definition, which is what is tested.
                    _unresolvable.Add(token);
                    return null;
                }
            }
        }

        /// <summary>
        /// Every type an assembly defines. A type that fails to load is reported rather than
        /// skipped: a silently missing type is exactly how one of these gates stops seeing
        /// anything.
        /// </summary>
        private static Type[] DefinedTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException failure)
            {
                var reasons = new List<string>();
                var loaderExceptions = failure.LoaderExceptions;
                for (var index = 0; index < loaderExceptions.Length; index++)
                {
                    if (loaderExceptions[index] != null)
                    {
                        reasons.Add(loaderExceptions[index].Message);
                    }
                }

                Assert.Fail($"{assembly.GetName().Name} did not load cleanly: {string.Join(" | ", reasons.ToArray())}");
                return new Type[0];
            }
        }

        private static List<string> ReferencedAssemblyNames(Assembly assembly)
        {
            var referenced = assembly.GetReferencedAssemblies();
            var names = new List<string>(referenced.Length);
            for (var index = 0; index < referenced.Length; index++)
            {
                names.Add(referenced[index].Name);
            }

            Assert.That(names, Is.Not.Empty,
                $"{assembly.GetName().Name} reported no references at all, so a gate reading it " +
                "can see nothing");
            return names;
        }

        private static List<string> ForbiddenReferences(
            List<string> references,
            string[] forbiddenPrefixes,
            string[] forbiddenNames)
        {
            var offenders = new List<string>();
            for (var index = 0; index < references.Count; index++)
            {
                var reference = references[index];
                for (var prefix = 0; prefix < forbiddenPrefixes.Length; prefix++)
                {
                    if (reference.StartsWith(forbiddenPrefixes[prefix], StringComparison.Ordinal))
                    {
                        offenders.Add(reference);
                    }
                }

                if (ContainsName(forbiddenNames, reference))
                {
                    offenders.Add(reference);
                }
            }

            return offenders;
        }

        /// <summary>
        /// The floating-point fields declared by one type, looking through an array element
        /// and a generic argument as well, since <c>List&lt;float&gt;</c> is exactly as
        /// divergent as a <c>float</c>.
        /// </summary>
        private static List<string> FloatingPointFields(Type type)
        {
            var found = new List<string>();
            var fields = type.GetFields(InstanceMembers);
            for (var index = 0; index < fields.Length; index++)
            {
                var field = fields[index];
                if (IsFloatingPoint(field.FieldType))
                {
                    found.Add($"{field.Name} : {field.FieldType.Name}");
                }
            }

            return found;
        }

        private static bool IsFloatingPoint(Type type)
        {
            if (type == null)
            {
                return false;
            }

            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            {
                return true;
            }

            if (type.IsArray)
            {
                return IsFloatingPoint(type.GetElementType());
            }

            if (!type.IsGenericType)
            {
                return false;
            }

            var arguments = type.GetGenericArguments();
            for (var index = 0; index < arguments.Length; index++)
            {
                if (IsFloatingPoint(arguments[index]))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSystemLinqType(Type type)
        {
            var namespaced = type == null ? null : type.Namespace;
            return namespaced == "System.Linq" ||
                   (namespaced != null &&
                    namespaced.StartsWith("System.Linq.", StringComparison.Ordinal));
        }

        private static bool IsNondeterministicType(Type type)
        {
            var full = type == null ? null : type.FullName;
            return full == "System.Random" ||
                   full == "System.DateTime" ||
                   full == "System.DateTimeOffset" ||
                   full == "UnityEngine.Random";
        }

        private static bool IsSeededRandomType(Type type) =>
            type != null && type.FullName == "System.Random";

        /// <summary>
        /// The determinism predicate, shared by the gate and by the control that proves the
        /// transport exemption is narrow: an exempt type is excused for the seeded
        /// <see cref="System.Random"/> draw only, and every other nondeterministic read it
        /// makes stays a finding. Anything a pardon covered is named in
        /// <paramref name="pardoned"/> so the gate can show which exemptions are load-bearing
        /// instead of guessing from a list that happened to come back empty.
        /// </summary>
        private static Func<Type, bool> NondeterminismPredicate(
            bool seededRandomExempted,
            List<string> pardoned = null) =>
            candidate =>
            {
                if (!IsNondeterministicType(candidate))
                {
                    return false;
                }

                if (!seededRandomExempted || !IsSeededRandomType(candidate))
                {
                    return true;
                }

                pardoned?.Add(candidate.FullName);
                return false;
            };

        /// <summary>
        /// The type a metadata token actually reaches. <c>ResolveMember</c> hands back a
        /// <see cref="Type"/> for a type token, and a top-level type has no
        /// <see cref="MemberInfo.DeclaringType"/>: reading that property alone sees nothing at
        /// a <c>typeof</c>, a cast, a box or a <c>newarr</c>, which is how a banned type can
        /// arrive in a body without ever being called.
        /// </summary>
        private static Type TargetTypeOf(MemberInfo member) => (member as Type) ?? member?.DeclaringType;

        /// <summary>
        /// Identity or wall-clock reachability inside the authoritative namespaces: the type
        /// itself for a clock or an entropy source, plus the one operation that mints a
        /// <see cref="Guid"/>. That type is the representation the session and match ids
        /// carry, so it is named by member rather than banned whole.
        /// </summary>
        private static bool IsIdentityOrClockTarget(MemberInfo member)
        {
            var target = TargetTypeOf(member);
            if (target == null)
            {
                return false;
            }

            return ContainsName(IdentityAndClockTypes, target.FullName) ||
                   ContainsName(EntropyMintingMembers, target.FullName + "." + member.Name);
        }

        /// <summary>
        /// Field, property and method-signature types. Cheap, and catches a banned type that
        /// is stored or passed around rather than called.
        /// </summary>
        private static List<string> BannedTypesInSignatures(
            Type type,
            Func<Type, bool> isBanned,
            string label)
        {
            var found = new List<string>();

            if (IsBanned(type.BaseType, isBanned) || IsBanned(type.DeclaringType, isBanned))
            {
                found.Add($"{label}: {type.FullName}");
            }

            var fields = type.GetFields(AllMembers);
            for (var index = 0; index < fields.Length; index++)
            {
                if (IsBanned(fields[index].FieldType, isBanned))
                {
                    found.Add($"{label}: {type.FullName}.{fields[index].Name}");
                }
            }

            var properties = type.GetProperties(AllMembers);
            for (var index = 0; index < properties.Length; index++)
            {
                if (IsBanned(properties[index].PropertyType, isBanned))
                {
                    found.Add($"{label}: {type.FullName}.{properties[index].Name}");
                }
            }

            var methods = type.GetMethods(AllMembers);
            for (var index = 0; index < methods.Length; index++)
            {
                var method = methods[index];
                if (IsBanned(method.ReturnType, isBanned))
                {
                    found.Add($"{label}: {type.FullName}.{method.Name}()");
                }

                var parameters = method.GetParameters();
                for (var parameter = 0; parameter < parameters.Length; parameter++)
                {
                    if (IsBanned(parameters[parameter].ParameterType, isBanned))
                    {
                        found.Add($"{label}: {type.FullName}.{method.Name}({parameters[parameter].Name})");
                    }
                }
            }

            return found;
        }

        private static bool IsBanned(Type candidate, Func<Type, bool> isBanned)
        {
            return candidate != null && isBanned(candidate);
        }

        /// <summary>
        /// Test every member token an IL stream hands to a call, a construction, a field
        /// access, a cast or a <c>ldtoken</c>.
        ///
        /// The stream is deliberately not decoded instruction by instruction: this reads the
        /// four bytes after any opcode byte that takes an inline member token, which
        /// over-approximates the set of operands. That is the safe direction for a gate. A
        /// missed instruction would be a hole in it, while a coincidental window can only
        /// ever resolve to a row the assembly really has — and if <c>System.Linq</c> or
        /// <c>System.Random</c> is in that table, the invariant the gate guards is already
        /// broken, which is the answer being asked for.
        /// </summary>
        private static void DecodeBodies(
            Type type,
            Func<MemberInfo, bool> isBannedMember,
            List<string> offenders,
            TokenCache cache,
            string label,
            out int decodedBodies)
        {
            var bodies = 0;

            var methods = type.GetMethods(AllMembers);
            for (var index = 0; index < methods.Length; index++)
            {
                bodies += ScanBody(methods[index], isBannedMember, offenders, cache, label);
            }

            var constructors = type.GetConstructors(AllMembers);
            for (var index = 0; index < constructors.Length; index++)
            {
                bodies += ScanBody(constructors[index], isBannedMember, offenders, cache, label);
            }

            decodedBodies = bodies;
        }

        private static int ScanBody(
            MethodBase method,
            Func<MemberInfo, bool> isBannedMember,
            List<string> offenders,
            TokenCache cache,
            string label)
        {
            MethodBody body;
            try
            {
                body = method.GetMethodBody();
            }
            catch (Exception)
            {
                // A declared-but-unimplemented or runtime-provided method has no IL to read.
                return 0;
            }

            if (body == null)
            {
                return 0;
            }

            var il = body.GetILAsByteArray();
            if (il == null || il.Length < 5)
            {
                return 0;
            }

            var module = method.Module;
            var owner = method.DeclaringType == null ? "?" : method.DeclaringType.FullName;

            for (var offset = 0; offset + 4 < il.Length; offset++)
            {
                var operand = offset + 1;
                if (il[offset] == TwoByteOpcodePrefix)
                {
                    // A two-byte opcode carries its token one further along, so the window
                    // has to be checked against the end of the body again before it is read.
                    if (operand + 4 >= il.Length || !TakesMemberTokenTwoByte(il[operand]))
                    {
                        continue;
                    }

                    operand++;
                }
                else if (!TakesMemberToken(il[offset]))
                {
                    continue;
                }

                var token = il[operand] | (il[operand + 1] << 8) |
                            (il[operand + 2] << 16) | (il[operand + 3] << 24);
                if (!IsResolvableToken(token))
                {
                    continue;
                }

                var member = cache.Resolve(module, token);
                if (member == null || !isBannedMember(member))
                {
                    continue;
                }

                var target = TargetTypeOf(member);
                var named = target == null ? "?" : target.FullName;
                offenders.Add($"{label}: {owner}.{method.Name} -> {named}.{member.Name}");
            }

            return 1;
        }

        /// <summary>
        /// The single-byte opcodes whose inline operand is a type, field or method token.
        /// A LINQ chain is always a <c>call</c> to a static
        /// <see cref="System.Linq.Enumerable"/> method and an unseeded RNG is always a
        /// <c>newobj</c> plus a <c>callvirt</c>, so this set covers every route a banned
        /// reference has into a body, and the bite pass proves it. The object and array
        /// opcodes are here as well: a <c>newarr</c>, a <c>box</c> or an <c>ldobj</c> names
        /// its type in the table without calling anything on it, and that is a reference all
        /// the same.
        /// </summary>
        private static bool TakesMemberToken(byte opcode)
        {
            switch (opcode)
            {
                case 0x28: // call
                case 0x6F: // callvirt
                case 0x73: // newobj
                case 0x76: // ldftn
                case 0x70: // cpobj
                case 0x71: // ldobj
                case 0x79: // unbox
                case 0x81: // stobj
                case 0x8D: // newarr
                case 0x8F: // ldelema
                case 0x74: // castclass
                case 0x75: // isinst
                case 0x7B: // ldfld
                case 0x7C: // ldflda
                case 0x7D: // stfld
                case 0x7E: // ldsfld
                case 0x7F: // ldsflda
                case 0x80: // stsfld
                case 0x8C: // box
                case 0xA5: // unbox.any
                case 0xD0: // ldtoken
                {
                    return true;
                }

                default:
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The second byte of a <c>0xFE</c> opcode whose inline operand is a type token.
        /// </summary>
        private static bool TakesMemberTokenTwoByte(byte secondByte)
        {
            switch (secondByte)
            {
                case 0x15: // initobj
                case 0x16: // constrained.
                case 0x1C: // sizeof
                {
                    return true;
                }

                default:
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The rows that name a type rather than a member. <c>ResolveType</c> is the entry
        /// point that answers for a <c>TypeDef</c>, and it is what hands back a
        /// <see cref="Type"/> for the cast, box and object opcodes above.
        /// </summary>
        private static bool IsTypeToken(int token)
        {
            var table = (token >> 24) & 0xFF;
            return table == 0x01 || // TypeRef
                   table == 0x02 || // TypeDef
                   table == 0x1B;   // TypeSpec
        }

        private const byte TwoByteOpcodePrefix = 0xFE;

        private static bool IsResolvableToken(int token)
        {
            var table = (token >> 24) & 0xFF;
            return table == 0x01 || // TypeRef
                   table == 0x02 || // TypeDef
                   table == 0x04 || // FieldDef
                   table == 0x06 || // MethodDef
                   table == 0x0A || // MemberRef
                   table == 0x1B || // TypeSpec
                   table == 0x2B;   // MethodSpec
        }

        private static bool IsAuthoritativeType(Type type)
        {
            if (type.FullName == AuthoritativeServerType)
            {
                return true;
            }

            var namespaced = type.Namespace;
            if (namespaced == null)
            {
                return false;
            }

            for (var index = 0; index < AuthoritativeNamespaces.Length; index++)
            {
                if (namespaced == AuthoritativeNamespaces[index] ||
                    namespaced.StartsWith(
                        AuthoritativeNamespaces[index] + ".", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A type by full name, found by sweeping the assembly rather than by
        /// <c>Assembly.GetType</c>, so a nested type and one declared in an internal file
        /// resolve just as well as a public one.
        /// </summary>
        private static Type FindType(Assembly assembly, string fullName)
        {
            var types = DefinedTypes(assembly);
            for (var index = 0; index < types.Length; index++)
            {
                if (types[index].FullName == fullName)
                {
                    return types[index];
                }
            }

            return null;
        }

        /// <summary>
        /// A type by its simple name out of an already-swept list, which is how the
        /// component exemptions are written: the point of those is a class a designer may
        /// stack, not a namespace it happens to sit in.
        /// </summary>
        private static Type FindNamedType(List<Type> types, string name)
        {
            for (var index = 0; index < types.Count; index++)
            {
                if (types[index].Name == name)
                {
                    return types[index];
                }
            }

            return null;
        }

        private static bool ContainsName(string[] names, string candidate)
        {
            if (candidate == null)
            {
                return false;
            }

            for (var index = 0; index < names.Length; index++)
            {
                if (names[index] == candidate)
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> TypeNames(List<Type> types)
        {
            var names = new List<string>(types.Count);
            for (var index = 0; index < types.Count; index++)
            {
                names.Add(types[index].Name);
            }

            return names;
        }

        private static string Describe(List<string> values)
        {
            if (values.Count == 0)
            {
                return "nothing";
            }

            var shown = Math.Min(8, values.Count);
            var text = string.Join(", ", values.GetRange(0, shown).ToArray());
            return values.Count > shown
                ? text + " (+" + (values.Count - shown) + " more)"
                : text;
        }
    }
}
