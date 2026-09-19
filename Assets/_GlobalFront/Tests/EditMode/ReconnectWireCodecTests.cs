using System;
using GlobalFront.Core.Model;
using GlobalFront.Core.Reconnect;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = NUnit.Framework.Is;

namespace GlobalFront.Tests.EditMode
{
    [TestFixture]
    public sealed class ReconnectWireCodecTests
    {
        private byte[] _testSecretBytes;
        private SessionSecret32 _testSecret;
        private SessionId _testSessionId;
        private MatchId _testMatchId;

        [SetUp]
        public void SetUp()
        {
            _testSecretBytes = new byte[32];
            for (int i = 0; i < 32; i++)
            {
                _testSecretBytes[i] = (byte)(0xA0 + i);
            }

            _testSecret = new SessionSecret32(_testSecretBytes);
            _testSessionId = new SessionId(Guid.NewGuid());
            _testMatchId = new MatchId(123456789012345ul);
        }

        [Test]
        public void ReconnectRequest_Roundtrip_Succeeds()
        {
            var original = new ReconnectRequest(
                ReconnectRequest.ExpectedOpcode,
                ReconnectRequest.ExpectedProtocolVersion,
                _testSessionId,
                _testSecret,
                lastAppliedTick: 54321ul);

            var buffer = new byte[ReconnectRequest.SizeBytes];
            var encodeOk = ReconnectWireCodec.TryEncodeRequest(original, buffer, out var written);

            Assert.That(encodeOk, Is.True);
            Assert.That(written, Is.EqualTo(ReconnectRequest.SizeBytes));
            Assert.That(buffer[0], Is.EqualTo(12));

            var decodeOk = ReconnectWireCodec.TryDecodeRequest(buffer, out var decoded);

            Assert.That(decodeOk, Is.True);
            Assert.That(decoded.Opcode, Is.EqualTo(original.Opcode));
            Assert.That(decoded.ProtocolVersion, Is.EqualTo(original.ProtocolVersion));
            Assert.That(decoded.SessionId, Is.EqualTo(original.SessionId));
            Assert.That(decoded.Secret, Is.EqualTo(original.Secret));
            Assert.That(decoded.LastAppliedTick, Is.EqualTo(original.LastAppliedTick));
            Assert.That(decoded.ReconnectSecret, Is.EqualTo(original.ReconnectSecret));
            Assert.That(decoded, Is.EqualTo(original));
        }

        [Test]
        public void ReconnectRequest_TruncatedBuffer_ReturnsFalse()
        {
            var request = new ReconnectRequest(_testSessionId, _testSecretBytes, 100ul);
            var buffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(ReconnectWireCodec.TryEncodeRequest(request, buffer, out _), Is.True);

            for (int len = 0; len < ReconnectRequest.SizeBytes; len++)
            {
                var truncated = buffer.AsSpan(0, len);
                Assert.That(
                    ReconnectWireCodec.TryEncodeRequest(request, buffer.AsSpan(0, len), out var written),
                    Is.False,
                    $"Encoding into length {len} should fail");
                Assert.That(written, Is.EqualTo(0));

                Assert.That(
                    ReconnectWireCodec.TryDecodeRequest(truncated, out _),
                    Is.False,
                    $"Decoding from length {len} should fail");
            }
        }

        [Test]
        public void ReconnectRequest_CorruptedBuffer_ReturnsFalse()
        {
            var request = new ReconnectRequest(_testSessionId, _testSecretBytes, 100ul);
            var buffer = new byte[ReconnectRequest.SizeBytes];
            Assert.That(ReconnectWireCodec.TryEncodeRequest(request, buffer, out _), Is.True);

            // Invalid opcode
            var badOpcode = (byte[])buffer.Clone();
            badOpcode[0] = 99;
            Assert.That(ReconnectWireCodec.TryDecodeRequest(badOpcode, out _), Is.False);

            // Invalid protocol version
            var badVersion = (byte[])buffer.Clone();
            badVersion[1] = 2;
            Assert.That(ReconnectWireCodec.TryDecodeRequest(badVersion, out _), Is.False);
        }

        [Test]
        public void ReconnectResponse_Roundtrip_AllResults_Succeeds()
        {
            var results = (ReconnectResult[])Enum.GetValues(typeof(ReconnectResult));

            foreach (var result in results)
            {
                var original = new ReconnectResponse(
                    ReconnectResponse.ExpectedOpcode,
                    result,
                    new PlayerId(3),
                    _testMatchId,
                    serverTick: 98765ul,
                    activeKeyframeSeq: 17);

                var buffer = new byte[ReconnectResponse.SizeBytes];
                var encodeOk = ReconnectWireCodec.TryEncodeResponse(original, buffer, out var written);

                Assert.That(encodeOk, Is.True);
                Assert.That(written, Is.EqualTo(ReconnectResponse.SizeBytes));
                Assert.That(buffer[0], Is.EqualTo(13));
                Assert.That(buffer[1], Is.EqualTo((byte)result));
                Assert.That(buffer[2], Is.EqualTo(3));

                var decodeOk = ReconnectWireCodec.TryDecodeResponse(buffer, out var decoded);

                Assert.That(decodeOk, Is.True);
                Assert.That(decoded.Opcode, Is.EqualTo(original.Opcode));
                Assert.That(decoded.Result, Is.EqualTo(original.Result));
                Assert.That(decoded.AssignedPlayer, Is.EqualTo(original.AssignedPlayer));
                Assert.That(decoded.Match, Is.EqualTo(original.Match));
                Assert.That(decoded.MatchValue, Is.EqualTo(original.MatchValue));
                Assert.That(decoded.ServerTick, Is.EqualTo(original.ServerTick));
                Assert.That(decoded.ActiveKeyframeSeq, Is.EqualTo(original.ActiveKeyframeSeq));
                Assert.That(decoded, Is.EqualTo(original));
            }
        }

        [Test]
        public void ReconnectResponse_TruncatedBuffer_ReturnsFalse()
        {
            var response = new ReconnectResponse(ReconnectResult.Accepted, new PlayerId(1), _testMatchId, 100ul, 1);
            var buffer = new byte[ReconnectResponse.SizeBytes];
            Assert.That(ReconnectWireCodec.TryEncodeResponse(response, buffer, out _), Is.True);

            for (int len = 0; len < ReconnectResponse.SizeBytes; len++)
            {
                Assert.That(
                    ReconnectWireCodec.TryEncodeResponse(response, buffer.AsSpan(0, len), out var written),
                    Is.False,
                    $"Encoding into length {len} should fail");
                Assert.That(written, Is.EqualTo(0));

                Assert.That(
                    ReconnectWireCodec.TryDecodeResponse(buffer.AsSpan(0, len), out _),
                    Is.False,
                    $"Decoding from length {len} should fail");
            }
        }

        [Test]
        public void ReconnectResponse_CorruptedBuffer_ReturnsFalse()
        {
            var response = new ReconnectResponse(ReconnectResult.Accepted, new PlayerId(1), _testMatchId, 100ul, 1);
            var buffer = new byte[ReconnectResponse.SizeBytes];
            Assert.That(ReconnectWireCodec.TryEncodeResponse(response, buffer, out _), Is.True);

            // Invalid opcode
            var badOpcode = (byte[])buffer.Clone();
            badOpcode[0] = 0;
            Assert.That(ReconnectWireCodec.TryDecodeResponse(badOpcode, out _), Is.False);

            // Invalid ReconnectResult enum (> 4)
            var badResult = (byte[])buffer.Clone();
            badResult[1] = 5;
            Assert.That(ReconnectWireCodec.TryDecodeResponse(badResult, out _), Is.False);
        }

        [Test]
        public void SessionSecret_Roundtrip_Succeeds()
        {
            var buffer = new byte[SessionSecretMessage.SizeBytes];
            var encodeOk = ReconnectWireCodec.TryEncodeSecret(_testSecretBytes, buffer, out var written);

            Assert.That(encodeOk, Is.True);
            Assert.That(written, Is.EqualTo(SessionSecretMessage.SizeBytes));
            Assert.That(buffer[0], Is.EqualTo(9));

            // Decode to byte[]
            var decodeOk = ReconnectWireCodec.TryDecodeSecret(buffer, out var decodedArray);
            Assert.That(decodeOk, Is.True);
            Assert.That(decodedArray, Is.EqualTo(_testSecretBytes));

            // Decode to Span (Zero-GC)
            Span<byte> destSpan = stackalloc byte[32];
            var decodeSpanOk = ReconnectWireCodec.TryDecodeSecret(buffer, destSpan);
            Assert.That(decodeSpanOk, Is.True);
            Assert.That(destSpan.SequenceEqual(_testSecretBytes), Is.True);

            // Decode to SessionSecretMessage struct
            var decodeMsgOk = ReconnectWireCodec.TryDecodeSecretMessage(buffer, out var message);
            Assert.That(decodeMsgOk, Is.True);
            Assert.That(message.Opcode, Is.EqualTo(9));
            Assert.That(message.Secret, Is.EqualTo(_testSecret));
        }

        [Test]
        public void SessionSecret_TruncatedOrInvalid_ReturnsFalse()
        {
            var buffer = new byte[SessionSecretMessage.SizeBytes];
            Assert.That(ReconnectWireCodec.TryEncodeSecret(_testSecretBytes, buffer, out _), Is.True);

            // Small source secret
            Assert.That(ReconnectWireCodec.TryEncodeSecret(new byte[31], buffer, out _), Is.False);

            // Small destination buffer
            Assert.That(ReconnectWireCodec.TryEncodeSecret(_testSecretBytes, buffer.AsSpan(0, 32), out _), Is.False);

            // Small decode buffer
            Assert.That(ReconnectWireCodec.TryDecodeSecret(buffer.AsSpan(0, 32), out byte[] _), Is.False);

            // Bad opcode
            var badOpcode = (byte[])buffer.Clone();
            badOpcode[0] = 8;
            Assert.That(ReconnectWireCodec.TryDecodeSecret(badOpcode, out byte[] _), Is.False);
        }

        [Test]
        public void FixedTimeEquals_MatchingAndMismatching_ReturnsExpected()
        {
            var a = (byte[])_testSecretBytes.Clone();
            var b = (byte[])_testSecretBytes.Clone();

            Assert.That(ReconnectWireCodec.FixedTimeEquals(a, b), Is.True);

            // Mismatch at start
            b[0] ^= 0x01;
            Assert.That(ReconnectWireCodec.FixedTimeEquals(a, b), Is.False);

            // Restore and mismatch at middle
            b[0] ^= 0x01;
            b[16] ^= 0x80;
            Assert.That(ReconnectWireCodec.FixedTimeEquals(a, b), Is.False);

            // Restore and mismatch at end
            b[16] ^= 0x80;
            b[31] ^= 0xFF;
            Assert.That(ReconnectWireCodec.FixedTimeEquals(a, b), Is.False);

            // Length mismatch
            Assert.That(ReconnectWireCodec.FixedTimeEquals(a.AsSpan(0, 16), b.AsSpan(0, 32)), Is.False);

            // Empty spans match
            Assert.That(ReconnectWireCodec.FixedTimeEquals(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty), Is.True);
        }

        [Test]
        public void ZeroGc_HotPaths_DoNotAllocateGCMemory()
        {
            var request = new ReconnectRequest(_testSessionId, _testSecretBytes, 100ul);
            var response = new ReconnectResponse(ReconnectResult.Accepted, new PlayerId(1), _testMatchId, 100ul, 1);
            var reqBuffer = new byte[ReconnectRequest.SizeBytes];
            var respBuffer = new byte[ReconnectResponse.SizeBytes];
            var secretBuffer = new byte[SessionSecretMessage.SizeBytes];
            var secretOutBuffer = new byte[SessionSecretMessage.SecretSizeBytes];

            // Warmup
            for (int i = 0; i < 50; i++)
            {
                ReconnectWireCodec.TryEncodeRequest(request, reqBuffer, out _);
                ReconnectWireCodec.TryDecodeRequest(reqBuffer, out _);

                ReconnectWireCodec.TryEncodeResponse(response, respBuffer, out _);
                ReconnectWireCodec.TryDecodeResponse(respBuffer, out _);

                ReconnectWireCodec.TryEncodeSecret(_testSecretBytes, secretBuffer, out _);
                ReconnectWireCodec.TryDecodeSecret(secretBuffer, secretOutBuffer);

                ReconnectWireCodec.FixedTimeEquals(secretOutBuffer, _testSecretBytes);
            }

            // Zero-GC verification for Request hot path
            Assert.That(
                () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        ReconnectWireCodec.TryEncodeRequest(request, reqBuffer, out _);
                        ReconnectWireCodec.TryDecodeRequest(reqBuffer, out _);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "ReconnectRequest encode and decode must be strictly zero-GC");

            // Zero-GC verification for Response hot path
            Assert.That(
                () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        ReconnectWireCodec.TryEncodeResponse(response, respBuffer, out _);
                        ReconnectWireCodec.TryDecodeResponse(respBuffer, out _);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "ReconnectResponse encode and decode must be strictly zero-GC");

            // Zero-GC verification for Secret span hot path
            Assert.That(
                () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        ReconnectWireCodec.TryEncodeSecret(_testSecretBytes, secretBuffer, out _);
                        ReconnectWireCodec.TryDecodeSecret(secretBuffer, secretOutBuffer);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "Secret span encode and decode must be strictly zero-GC");

            // Zero-GC verification for FixedTimeEquals
            Assert.That(
                () =>
                {
                    for (int i = 0; i < 100; i++)
                    {
                        ReconnectWireCodec.FixedTimeEquals(secretOutBuffer, _testSecretBytes);
                    }
                },
                UnityEngine.TestTools.Constraints.Is.Not.AllocatingGCMemory(),
                "FixedTimeEquals must be strictly zero-GC");
        }
    }
}
