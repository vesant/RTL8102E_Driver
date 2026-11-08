using System;
using System.Text;

namespace RTL8102E_Driver
{
    public enum Wpa2HandshakeState
    {
        Idle,
        ConnectingAuthAssoc,
        WaitingMsg1,
        Msg2Sent,
        WaitingMsg3,
        Completed,
        Failed
    }

    /// <summary>
    /// Pure C# RFC 3174 SHA-1 implementation without any System.Security.Cryptography dependencies.
    /// Perfectly compatible with Cosmos OS / IL2CPU bare-metal runtime.
    /// </summary>
    public class PureSha1
    {
        private uint[] _h = new uint[5];
        private byte[] _buffer = new byte[64];
        private int _bufferLength;
        private ulong _totalBytes;
        private uint[] _w = new uint[80];

        public PureSha1()
        {
            Reset();
        }

        public void Reset()
        {
            _h[0] = 0x67452301u;
            _h[1] = 0xEFCDAB89u;
            _h[2] = 0x98BADCFEu;
            _h[3] = 0x10325476u;
            _h[4] = 0xC3D2E1F0u;
            _bufferLength = 0;
            _totalBytes = 0;
        }

        public void Update(byte[] data, int offset, int count)
        {
            if (data == null || count <= 0) return;
            _totalBytes += (ulong)count;

            int i = 0;
            if (_bufferLength > 0)
            {
                int toCopy = Math.Min(64 - _bufferLength, count);
                Array.Copy(data, offset, _buffer, _bufferLength, toCopy);
                _bufferLength += toCopy;
                i += toCopy;

                if (_bufferLength == 64)
                {
                    ProcessBlock(_buffer, 0);
                    _bufferLength = 0;
                }
            }

            while (i + 64 <= count)
            {
                ProcessBlock(data, offset + i);
                i += 64;
            }

            if (i < count)
            {
                int remaining = count - i;
                Array.Copy(data, offset + i, _buffer, 0, remaining);
                _bufferLength = remaining;
            }
        }

        public void Final(byte[] output, int outOffset)
        {
            ulong totalBits = _totalBytes * 8;

            _buffer[_bufferLength++] = 0x80;

            if (_bufferLength > 56)
            {
                while (_bufferLength < 64) _buffer[_bufferLength++] = 0;
                ProcessBlock(_buffer, 0);
                _bufferLength = 0;
            }

            while (_bufferLength < 56) _buffer[_bufferLength++] = 0;

            _buffer[56] = (byte)(totalBits >> 56);
            _buffer[57] = (byte)(totalBits >> 48);
            _buffer[58] = (byte)(totalBits >> 40);
            _buffer[59] = (byte)(totalBits >> 32);
            _buffer[60] = (byte)(totalBits >> 24);
            _buffer[61] = (byte)(totalBits >> 16);
            _buffer[62] = (byte)(totalBits >> 8);
            _buffer[63] = (byte)(totalBits);

            ProcessBlock(_buffer, 0);

            for (int i = 0; i < 5; i++)
            {
                output[outOffset + i * 4] = (byte)(_h[i] >> 24);
                output[outOffset + i * 4 + 1] = (byte)(_h[i] >> 16);
                output[outOffset + i * 4 + 2] = (byte)(_h[i] >> 8);
                output[outOffset + i * 4 + 3] = (byte)(_h[i]);
            }
        }

        private void ProcessBlock(byte[] block, int offset)
        {
            for (int t = 0; t < 16; t++)
            {
                int idx = offset + t * 4;
                _w[t] = ((uint)block[idx] << 24) |
                        ((uint)block[idx + 1] << 16) |
                        ((uint)block[idx + 2] << 8) |
                        (uint)block[idx + 3];
            }

            for (int t = 16; t < 80; t++)
            {
                uint temp = _w[t - 3] ^ _w[t - 8] ^ _w[t - 14] ^ _w[t - 16];
                _w[t] = (temp << 1) | (temp >> 31);
            }

            uint a = _h[0];
            uint b = _h[1];
            uint c = _h[2];
            uint d = _h[3];
            uint e = _h[4];

            for (int t = 0; t < 80; t++)
            {
                uint f, k;
                if (t < 20)
                {
                    f = (b & c) | ((~b) & d);
                    k = 0x5A827999u;
                }
                else if (t < 40)
                {
                    f = b ^ c ^ d;
                    k = 0x6ED9EBA1u;
                }
                else if (t < 60)
                {
                    f = (b & c) | (b & d) | (c & d);
                    k = 0x8F1BBCDCu;
                }
                else
                {
                    f = b ^ c ^ d;
                    k = 0xCA62C1D6u;
                }

                uint temp = ((a << 5) | (a >> 27)) + f + e + k + _w[t];
                e = d;
                d = c;
                c = (b << 30) | (b >> 2);
                b = a;
                a = temp;
            }

            _h[0] += a;
            _h[1] += b;
            _h[2] += c;
            _h[3] += d;
            _h[4] += e;
        }

        public static byte[] Hash(byte[] data)
        {
            var sha = new PureSha1();
            sha.Update(data, 0, data != null ? data.Length : 0);
            byte[] res = new byte[20];
            sha.Final(res, 0);
            return res;
        }
    }

    /// <summary>
    /// Pure C# RFC 2104 HMAC-SHA1 implementation with zero heap allocations during iterations.
    /// </summary>
    public class PureHmacSha1
    {
        private PureSha1 _sha = new PureSha1();
        private byte[] _keyPad = new byte[64];
        private byte[] _innerHash = new byte[20];
        private byte[] _ipad = new byte[64];
        private byte[] _opad = new byte[64];

        public void ComputeHmac(byte[] key, byte[] data, int dataOffset, int dataLen, byte[] output, int outOffset)
        {
            Array.Clear(_keyPad, 0, 64);
            if (key != null)
            {
                if (key.Length > 64)
                {
                    byte[] kh = PureSha1.Hash(key);
                    Array.Copy(kh, 0, _keyPad, 0, 20);
                }
                else
                {
                    Array.Copy(key, 0, _keyPad, 0, key.Length);
                }
            }

            for (int i = 0; i < 64; i++)
            {
                _ipad[i] = (byte)(_keyPad[i] ^ 0x36);
                _opad[i] = (byte)(_keyPad[i] ^ 0x5C);
            }

            _sha.Reset();
            _sha.Update(_ipad, 0, 64);
            if (data != null && dataLen > 0)
            {
                _sha.Update(data, dataOffset, dataLen);
            }
            _sha.Final(_innerHash, 0);

            _sha.Reset();
            _sha.Update(_opad, 0, 64);
            _sha.Update(_innerHash, 0, 20);
            _sha.Final(output, outOffset);
        }

        public static byte[] Compute(byte[] key, byte[] data)
        {
            var hmac = new PureHmacSha1();
            byte[] res = new byte[20];
            hmac.ComputeHmac(key, data, 0, data != null ? data.Length : 0, res, 0);
            return res;
        }
    }

    /// <summary>
    /// Pure C# RFC 2898 / PKCS#5 v2.0 PBKDF2-HMAC-SHA1 for deriving 32-byte PMK.
    /// </summary>
    public static class PurePbkdf2
    {
        public static byte[] DerivePmk(string passphrase, string ssid)
        {
            byte[] passBytes = Encoding.UTF8.GetBytes(passphrase ?? "");
            byte[] ssidBytes = Encoding.UTF8.GetBytes(ssid ?? "");
            return DeriveKey(passBytes, ssidBytes, 4096, 32);
        }

        public static byte[] DeriveKey(byte[] password, byte[] salt, int iterations, int keyLen)
        {
            byte[] output = new byte[keyLen];
            var hmac = new PureHmacSha1();
            byte[] saltPlusBlock = new byte[salt.Length + 4];
            Array.Copy(salt, 0, saltPlusBlock, 0, salt.Length);

            byte[] uBlock = new byte[20];
            byte[] uPrev = new byte[20];
            byte[] uCur = new byte[20];

            int totalBlocks = (keyLen + 19) / 20;
            int outOffset = 0;

            for (int block = 1; block <= totalBlocks; block++)
            {
                saltPlusBlock[salt.Length] = (byte)(block >> 24);
                saltPlusBlock[salt.Length + 1] = (byte)(block >> 16);
                saltPlusBlock[salt.Length + 2] = (byte)(block >> 8);
                saltPlusBlock[salt.Length + 3] = (byte)block;

                hmac.ComputeHmac(password, saltPlusBlock, 0, saltPlusBlock.Length, uPrev, 0);
                Array.Copy(uPrev, 0, uBlock, 0, 20);

                for (int iter = 2; iter <= iterations; iter++)
                {
                    hmac.ComputeHmac(password, uPrev, 0, 20, uCur, 0);
                    for (int x = 0; x < 20; x++)
                    {
                        uBlock[x] ^= uCur[x];
                        uPrev[x] = uCur[x];
                    }
                }

                int toCopy = Math.Min(20, keyLen - outOffset);
                Array.Copy(uBlock, 0, output, outOffset, toCopy);
                outOffset += toCopy;
            }

            return output;
        }
    }

    /// <summary>
    /// Pure C# IEEE 802.11i-2004 PRF-512 Pseudo-Random Function for PTK derivation.
    /// </summary>
    public static class PurePrf512
    {
        public static byte[] DerivePtk(byte[] pmk, byte[] aa, byte[] spa, byte[] anonce, byte[] snonce)
        {
            byte[] label = Encoding.ASCII.GetBytes("Pairwise key expansion");

            // Lexicographical order: Min(AA, SPA) || Max(AA, SPA)
            byte[] minMac = LessThan(aa, spa) ? aa : spa;
            byte[] maxMac = LessThan(aa, spa) ? spa : aa;

            // Lexicographical order: Min(ANonce, SNonce) || Max(ANonce, SNonce)
            byte[] minNonce = LessThan(anonce, snonce) ? anonce : snonce;
            byte[] maxNonce = LessThan(anonce, snonce) ? snonce : anonce;

            byte[] dataB = new byte[12 + 64];
            Array.Copy(minMac, 0, dataB, 0, 6);
            Array.Copy(maxMac, 0, dataB, 6, 6);
            Array.Copy(minNonce, 0, dataB, 12, 32);
            Array.Copy(maxNonce, 0, dataB, 44, 32);

            byte[] input = new byte[label.Length + 1 + dataB.Length + 1];
            Array.Copy(label, 0, input, 0, label.Length);
            input[label.Length] = 0x00;
            Array.Copy(dataB, 0, input, label.Length + 1, dataB.Length);

            byte[] ptk = new byte[64];
            byte[] hmacOut = new byte[20];
            var hmac = new PureHmacSha1();

            for (byte i = 0; i < 4; i++)
            {
                input[input.Length - 1] = i;
                hmac.ComputeHmac(pmk, input, 0, input.Length, hmacOut, 0);
                int copyLen = Math.Min(20, 64 - i * 20);
                Array.Copy(hmacOut, 0, ptk, i * 20, copyLen);
            }

            return ptk;
        }

        private static bool LessThan(byte[] a, byte[] b)
        {
            int len = Math.Min(a.Length, b.Length);
            for (int i = 0; i < len; i++)
            {
                if (a[i] < b[i]) return true;
                if (a[i] > b[i]) return false;
            }
            return a.Length < b.Length;
        }
    }

    /// <summary>
    /// Pure bare-metal IEEE 802.11i WPA2-PSK 4-Way Handshake Engine.
    /// Authenticates with modern WPA2-PSK access points and phone hotspots.
    /// Derives PMK, PTK, KCK, KEK, and Temporal Key (TK), sends valid EAPOL Messages 2 & 4,
    /// verifies AP Message 3 MIC, and rejects incorrect passwords.
    /// </summary>
    public class Wpa2HandshakeEngine
    {
        public Wpa2HandshakeState State { get; set; } = Wpa2HandshakeState.Idle;
        public string FailureReason { get; set; } = "";

        public byte[] ApBssid { get; private set; } = new byte[6];
        public byte[] StationMac { get; private set; } = new byte[6];
        public string Ssid { get; private set; } = "";
        public string Passphrase { get; private set; } = "";
        public int Channel { get; private set; } = 1;

        public byte[] Pmk { get; private set; } = new byte[32];
        public byte[] Ptk { get; private set; } = new byte[64];
        public byte[] Kck { get; private set; } = new byte[16];
        public byte[] Kek { get; private set; } = new byte[16];
        public byte[] Tk { get; private set; } = new byte[16];

        public byte[] ANonce { get; private set; } = new byte[32];
        public byte[] SNonce { get; private set; } = new byte[32];
        public byte[] Msg1ReplayCounter { get; private set; } = new byte[8];
        public byte[] Msg3ReplayCounter { get; private set; } = new byte[8];

        public ushort SequenceNumber = 1;

        // Station RSN Information Element (Tag 48, Length 20)
        private static readonly byte[] StationRsnIe = new byte[] {
            0x30, 0x14,
            0x01, 0x00,                         // Version: 1
            0x00, 0x0F, 0xAC, 0x04,             // Group Cipher: AES-CCMP
            0x01, 0x00, 0x00, 0x0F, 0xAC, 0x04, // Pairwise Cipher: 1 AES-CCMP
            0x01, 0x00, 0x00, 0x0F, 0xAC, 0x02, // AKM Suite: 1 PSK
            0x00, 0x00                          // RSN Capabilities
        };

        public void Initialize(byte[] apBssid, byte[] stationMac, string ssid, string passphrase, int channel)
        {
            Array.Copy(apBssid, 0, ApBssid, 0, 6);
            Array.Copy(stationMac, 0, StationMac, 0, 6);
            Ssid = ssid ?? "";
            Passphrase = passphrase ?? "";
            Channel = channel;
            State = Wpa2HandshakeState.ConnectingAuthAssoc;
            FailureReason = "";

            // Derive 32-byte PMK using PBKDF2 (4096 iterations)
            Pmk = PurePbkdf2.DerivePmk(Passphrase, Ssid);
        }

        public static byte[] GenerateSNonce(byte[] stationMac, byte[] apBssid, uint tsfL, uint tsfU)
        {
            byte[] entropy = new byte[36];
            for (int i = 0; i < 6; i++)
            {
                entropy[i] = stationMac != null && i < stationMac.Length ? stationMac[i] : (byte)0x11;
                entropy[6 + i] = apBssid != null && i < apBssid.Length ? apBssid[i] : (byte)0x22;
            }
            entropy[12] = (byte)(tsfL);
            entropy[13] = (byte)(tsfL >> 8);
            entropy[14] = (byte)(tsfL >> 16);
            entropy[15] = (byte)(tsfL >> 24);
            entropy[16] = (byte)(tsfU);
            entropy[17] = (byte)(tsfU >> 8);
            entropy[18] = (byte)(tsfU >> 16);
            entropy[19] = (byte)(tsfU >> 24);
            entropy[20] = 0xAA; entropy[21] = 0x55; entropy[22] = 0x12; entropy[23] = 0x34;
            entropy[24] = 0x56; entropy[25] = 0x78; entropy[26] = 0x9A; entropy[27] = 0xBC;
            entropy[28] = 0xDE; entropy[29] = 0xF0; entropy[30] = 0xFE; entropy[31] = 0xED;
            entropy[32] = 0xCA; entropy[33] = 0xFE; entropy[34] = 0xBA; entropy[35] = 0xBE;

            byte[] h1 = PureSha1.Hash(entropy);
            for (int i = 0; i < 36; i++) entropy[i] ^= 0x5C;
            byte[] h2 = PureSha1.Hash(entropy);

            byte[] snonce = new byte[32];
            Array.Copy(h1, 0, snonce, 0, 20);
            Array.Copy(h2, 0, snonce, 20, 12);
            return snonce;
        }

        public bool ProcessEapolFrame(byte[] pkt, int eapolOffset, uint tsfL, uint tsfU, out byte[] responseFrame)
        {
            responseFrame = null;
            if (pkt == null || eapolOffset + 4 > pkt.Length) return false;

            byte eapolType = pkt[eapolOffset + 1];
            if (eapolType != 3) return false; // 3 = EAPOL-Key

            int bodyLen = (pkt[eapolOffset + 2] << 8) | pkt[eapolOffset + 3];
            if (eapolOffset + 4 + bodyLen > pkt.Length) return false;

            int kbOffset = eapolOffset + 4;
            byte descType = pkt[kbOffset];
            if (descType != 2 && descType != 254) return false; // 2 = RSN, 254 = WPA

            ushort keyInfo = (ushort)((pkt[kbOffset + 1] << 8) | pkt[kbOffset + 2]);
            bool isPairwise = (keyInfo & (1 << 3)) != 0;
            bool isInstall = (keyInfo & (1 << 6)) != 0;
            bool isAck = (keyInfo & (1 << 7)) != 0;
            bool isMic = (keyInfo & (1 << 8)) != 0;

            // Handle Message 1 (Pairwise=1, Ack=1, MIC=0)
            if (isPairwise && isAck && !isMic)
            {
                // Replay counter (8 bytes at kbOffset + 5)
                for (int i = 0; i < 8; i++) Msg1ReplayCounter[i] = pkt[kbOffset + 5 + i];

                // ANonce (32 bytes at kbOffset + 13)
                for (int i = 0; i < 32; i++) ANonce[i] = pkt[kbOffset + 13 + i];

                // Generate SNonce
                SNonce = GenerateSNonce(StationMac, ApBssid, tsfL, tsfU);

                // Derive PTK (64 bytes)
                Ptk = PurePrf512.DerivePtk(Pmk, ApBssid, StationMac, ANonce, SNonce);
                Array.Copy(Ptk, 0, Kck, 0, 16);
                Array.Copy(Ptk, 16, Kek, 0, 16);
                Array.Copy(Ptk, 32, Tk, 0, 16);

                // Construct Message 2
                responseFrame = BuildMessage2();
                State = Wpa2HandshakeState.Msg2Sent;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [EAPOL] Message 1/4 received from AP! Sent Message 2/4 (with computed MIC).");
                Console.ForegroundColor = ConsoleColor.Cyan;
                return true;
            }

            // Handle Message 3 (Pairwise=1, Ack=1, MIC=1)
            if (isPairwise && isAck && isMic && (State == Wpa2HandshakeState.Msg2Sent || State == Wpa2HandshakeState.WaitingMsg3))
            {
                for (int i = 0; i < 8; i++) Msg3ReplayCounter[i] = pkt[kbOffset + 5 + i];

                // Extract AP MIC (16 bytes at kbOffset + 77)
                byte[] apMic = new byte[16];
                for (int i = 0; i < 16; i++) apMic[i] = pkt[kbOffset + 77 + i];

                // Zero out MIC in a copy of the packet and verify HMAC-SHA1
                int eapolPacketLen = 4 + bodyLen;
                byte[] verifyBuf = new byte[eapolPacketLen];
                Array.Copy(pkt, eapolOffset, verifyBuf, 0, eapolPacketLen);
                for (int i = 0; i < 16; i++) verifyBuf[4 + 77 + i] = 0;

                byte[] computedHmac = PureHmacSha1.Compute(Kck, verifyBuf);
                bool valid = true;
                for (int i = 0; i < 16; i++)
                {
                    if (computedHmac[i] != apMic[i])
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    State = Wpa2HandshakeState.Failed;
                    FailureReason = "AP Message 3 MIC verification failed! Incorrect passphrase.";
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("  [EAPOL] Message 3/4 MIC mismatch! Password incorrect.");
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    return false;
                }

                // AP Message 3 verified! Build Message 4
                responseFrame = BuildMessage4();
                State = Wpa2HandshakeState.Completed;
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [EAPOL] Message 3/4 verified! AP acknowledged station. Sent Message 4/4.");
                Console.ForegroundColor = ConsoleColor.Cyan;
                return true;
            }

            return false;
        }

        public byte[] BuildMessage2()
        {
            int rsnLen = StationRsnIe.Length; // 22 bytes
            int bodyLen = 95 + rsnLen;       // 117 bytes
            int eapolLen = 4 + bodyLen;      // 121 bytes

            byte[] eapol = new byte[eapolLen];
            eapol[0] = 0x01; // Version 1
            eapol[1] = 0x03; // EAPOL-Key
            eapol[2] = (byte)(bodyLen >> 8);
            eapol[3] = (byte)(bodyLen & 0xFF);

            int kb = 4;
            eapol[kb] = 0x02; // Descriptor Type: RSN
            // Key Info: Pairwise=1 (bit 3), MIC=1 (bit 8), Version=2 (bits 0-2) -> 0x010A
            eapol[kb + 1] = 0x01;
            eapol[kb + 2] = 0x0A;
            eapol[kb + 3] = 0x00; // Key Length: 0
            eapol[kb + 4] = 0x00;

            // Replay Counter matching Message 1
            for (int i = 0; i < 8; i++) eapol[kb + 5 + i] = Msg1ReplayCounter[i];

            // SNonce
            for (int i = 0; i < 32; i++) eapol[kb + 13 + i] = SNonce[i];

            // Key IV (16 bytes = 0), RSC (8 bytes = 0), Reserved (8 bytes = 0), MIC (16 bytes = 0)
            // Array defaults to 0.

            // Key Data Length (22 bytes)
            eapol[kb + 93] = (byte)(rsnLen >> 8);
            eapol[kb + 94] = (byte)(rsnLen & 0xFF);

            // Key Data: Station RSN IE
            Array.Copy(StationRsnIe, 0, eapol, kb + 95, rsnLen);

            // Compute 16-byte HMAC-SHA1 MIC over whole EAPOL frame
            byte[] mic = PureHmacSha1.Compute(Kck, eapol);
            Array.Copy(mic, 0, eapol, kb + 77, 16);

            return WrapIn80211DataFrame(eapol);
        }

        public byte[] BuildMessage4()
        {
            int bodyLen = 95;           // 95 bytes (Key Data Length = 0)
            int eapolLen = 4 + bodyLen; // 99 bytes

            byte[] eapol = new byte[eapolLen];
            eapol[0] = 0x01; // Version 1
            eapol[1] = 0x03; // EAPOL-Key
            eapol[2] = (byte)(bodyLen >> 8);
            eapol[3] = (byte)(bodyLen & 0xFF);

            int kb = 4;
            eapol[kb] = 0x02; // Descriptor Type: RSN
            // Key Info: Pairwise=1 (bit 3), MIC=1 (bit 8), Secure=1 (bit 9), Version=2 (bits 0-2) -> 0x030A
            eapol[kb + 1] = 0x03;
            eapol[kb + 2] = 0x0A;
            eapol[kb + 3] = 0x00; // Key Length: 0
            eapol[kb + 4] = 0x00;

            // Replay Counter matching Message 3
            for (int i = 0; i < 8; i++) eapol[kb + 5 + i] = Msg3ReplayCounter[i];

            // SNonce is 0 in Message 4
            // Key IV, RSC, Reserved, MIC are 0.
            eapol[kb + 93] = 0x00; // Key Data Length = 0
            eapol[kb + 94] = 0x00;

            // Compute 16-byte HMAC-SHA1 MIC
            byte[] mic = PureHmacSha1.Compute(Kck, eapol);
            Array.Copy(mic, 0, eapol, kb + 77, 16);

            return WrapIn80211DataFrame(eapol);
        }

        private byte[] WrapIn80211DataFrame(byte[] eapolPayload)
        {
            // Standard 802.11 Data Frame (compatible with all APs & phone hotspots):
            // 24 bytes MAC header + 8 bytes LLC/SNAP + EAPOL
            int totalLen = 24 + 8 + eapolPayload.Length;
            byte[] frame = new byte[totalLen];

            // Frame Control: Data (0x08), ToDS=1 (0x01)
            frame[0] = 0x08;
            frame[1] = 0x01;
            frame[2] = 0x00; // Duration
            frame[3] = 0x00;

            // Address 1: Receiver / AP BSSID
            Array.Copy(ApBssid, 0, frame, 4, 6);
            // Address 2: Transmitter / Station MAC
            Array.Copy(StationMac, 0, frame, 10, 6);
            // Address 3: Destination / AP BSSID
            Array.Copy(ApBssid, 0, frame, 16, 6);

            // Sequence Control
            ushort seq = (ushort)((SequenceNumber++ & 0x0FFF) << 4);
            frame[22] = (byte)(seq & 0xFF);
            frame[23] = (byte)(seq >> 8);

            // LLC / SNAP Header (802.1X EtherType 0x888E)
            frame[24] = 0xAA;
            frame[25] = 0xAA;
            frame[26] = 0x03;
            frame[27] = 0x00;
            frame[28] = 0x00;
            frame[29] = 0x00;
            frame[30] = 0x88;
            frame[31] = 0x8E;

            // EAPOL Payload
            Array.Copy(eapolPayload, 0, frame, 32, eapolPayload.Length);

            return frame;
        }
    }
}
