using System;
using System.Collections.Generic;
using Cosmos.Core;
using Cosmos.HAL;

namespace RTL8102E_Driver
{
    /// <summary>
    /// Bare-metal Atheros AR2425 / AR5007EG / AR5212 IEEE 802.11b/g Wireless Driver.
    /// Supports PCI/PCIe Vendor 0x168C (Atheros AR2425, AR5212, AR2413, AR5413, AR2417).
    /// Performs direct 32-bit MMIO register control, EEPROM reads, RF synthesis tuning (2.4 GHz Ch 1-13),
    /// RX DMA descriptor management, and real-time 802.11 Beacon / Probe Response frame parsing.
    /// Pure polling mode - 0 interrupts, 0 Thread.Sleep to prevent lockups on Intel Atom N270.
    /// </summary>
    [Cosmos.HAL.Driver(Cosmos.HAL.DriverType.Network, "Atheros AR2425/AR5007EG 802.11b/g Wireless")]
    [Cosmos.HAL.Author("ghost")]
    public class AtherosAR2425Driver
    {
        public PCIDevice PciDev { get; private set; }
        public uint MmioBase { get; private set; }
        public MemoryBlock Mmio { get; private set; }
        public byte[] MacAddress { get; private set; } = new byte[6];
        public bool IsInitialized { get; private set; } = false;
        public uint MacSrev { get; private set; }
        public uint PhyChipId { get; private set; }
        public ushort EepromVersion { get; private set; }
        public ushort EepromRegDomain { get; private set; }
        public int CurrentChannel { get; private set; } = 1;
        public uint RxPacketsCount { get; private set; } = 0;
        public uint TxPacketsCount { get; private set; } = 0;
        public uint BeaconFramesCount { get; private set; } = 0;
        public string HardwareSummary { get; private set; } = "Not detected";

        public Wpa2HandshakeEngine Wpa2Engine;
        public const uint AR5K_TSF_L32 = 0x804C;
        public const uint AR5K_TSF_U32 = 0x8050;
        public const uint AR5K_KEYTABLE_VALID = 0x00008000;
        public const uint AR5K_KEYTABLE_TYPE_CCM = 0x00000006;
        public const uint AR5K_KEYTABLE_TYPE_NULL = 0x00000007;

        // RX DMA Descriptors (16 descriptors)
        private const int RX_DESC_COUNT = 16;
        private const int RX_BUF_SIZE = 2048;
        private ManagedMemoryBlock _rxDescMem;
        private ManagedMemoryBlock _rxBufferMem;
        private uint _rxDescPhys;
        private uint _rxBufferPhys;
        private int _rxHead = 0;

        // TX DMA Descriptors (4 descriptors)
        private const int TX_DESC_COUNT = 4;
        private const int TX_BUF_SIZE = 512;
        private ManagedMemoryBlock _txDescMem;
        private ManagedMemoryBlock _txBufferMem;
        private uint _txDescPhys;
        private uint _txBufferPhys;
        private int _txHead = 0;

        // Detected Wi-Fi networks dictionary (BSSID -> WifiAccessPoint)
        public Dictionary<string, WifiAccessPoint> DetectedAps = new Dictionary<string, WifiAccessPoint>();

        // Register Addresses
        private const uint AR5K_CR = 0x0008;
        private const uint AR5K_CR_RXE = 0x00000004;
        private const uint AR5K_CR_TXE = 0x00000001;
        private const uint AR5K_CR_RXD = 0x00000020;
        private const uint AR5K_CR_TXD = 0x00000010;

        private const uint AR5K_CFG = 0x0014;
        private const uint AR5K_IER = 0x0024;
        private const uint AR5K_TXCFG = 0x0030;
        private const uint AR5K_RXCFG = 0x0034;
        private const uint AR5K_RXDP = 0x000C;

        private const uint AR5K_QCU_TXE = 0x0840;
        private const uint AR5K_QUEUE_TXDP_0 = 0x0800;
        private const uint AR5K_QUEUE_MISC_0 = 0x09C0;
        private const uint AR5K_QUEUE_DFS_LOCAL_IFS_0 = 0x1040;
        private const uint AR5K_QUEUE_DFS_RETRY_LIMIT_0 = 0x1080;
        private const uint AR5K_QUEUE_QCUMASK_0 = 0x1000;

        private const uint AR5K_RESET_CTL = 0x4000;
        private const uint AR5K_SLEEP_CTL = 0x4004;
        private const uint AR5K_PCICFG = 0x4010;
        private const uint AR5K_PCIE_SERDES = 0x4040;
        private const uint AR5K_PCIE_SERDES_RESET = 0x4044;

        private const uint AR5K_STA_ID0 = 0x8000;
        private const uint AR5K_STA_ID1 = 0x8004;
        private const uint AR5K_BSS_ID0 = 0x8008;
        private const uint AR5K_BSS_ID1 = 0x800C;
        private const uint AR5K_RX_FILTER = 0x803C;
        private const uint AR5K_MCAST_FILTER0 = 0x8040;
        private const uint AR5K_MCAST_FILTER1 = 0x8044;
        private const uint AR5K_DIAG_SW_5211 = 0x8048;
        private const uint AR5K_DEFAULT_ANTENNA = 0x80D0;
        private const uint AR5K_BSS_IDM0 = 0x80E0;
        private const uint AR5K_BSS_IDM1 = 0x80E4;

        private const uint AR5K_EEPROM_BASE = 0x6000;
        private const uint AR5K_EEPROM_DATA_5211 = 0x6004;
        private const uint AR5K_EEPROM_CMD = 0x6008;
        private const uint AR5K_EEPROM_STAT_5211 = 0x600C;

        private const uint AR5K_PHY_0 = 0x9800;
        private const uint AR5K_PHY_TURBO = 0x9804;
        private const uint AR5K_PHY_TIMING_3 = 0x9814;
        private const uint AR5K_PHY_ACT = 0x981C;
        private const uint AR5K_PHY_RF_CTL3 = 0x9828;
        private const uint AR5K_PHY_RF_CTL4 = 0x9834;
        private const uint AR5K_PHY_SETTLING = 0x9844;
        private const uint AR5K_PHY_GAIN = 0x9848;
        private const uint AR5K_PHY_DESIRED_SIZE = 0x9850;
        private const uint AR5K_PHY_AGCCTL = 0x9860;
        private const uint AR5K_PHY_NF62 = 0x9864;
        private const uint AR5K_PHY_SCAL = 0x9878;
        private const uint AR5K_PHY_PLL = 0x987C;
        private const uint AR5K_RF_BUFFER = 0x989C;
        private const uint AR5K_RF_BUFFER_CONTROL_5 = 0x98D8;
        private const uint AR5K_PHY_ANT_SWITCH_TABLE_0 = 0x9910;
        private const uint AR5K_PHY_ANT_SWITCH_TABLE_1 = 0x9960;
        private const uint AR5K_PHY_ANT_CTL = 0x9964;
        private const uint AR5K_PHY_NFTHRES = 0x9968;
        private const uint AR5K_PHY_MODE = 0xA200;
        private const uint AR5K_PHY_CCKTXCTL = 0xA204;
        private const uint AR5K_PHY_FAST_ADC = 0xA24C;
        private const uint AR5K_PHY_CHIP_ID = 0x9818;

        public bool FindAndInitialize()
        {
            Console.WriteLine("ath5k: searching for Atheros 802.11 Wireless LAN device on PCI bus...");
            PCIDevice foundDev = null;

            foreach (var dev in PCI.Devices)
            {
                if (dev.VendorID == 0x168C)
                {
                    foundDev = dev;
                    break;
                }
            }

            if (foundDev == null)
            {
                Console.WriteLine("ath5k: no Atheros PCI device (Vendor 0x168C) found.");
                HardwareSummary = "Nenhum chip Atheros (0x168C) no PCI";
                return false;
            }

            PciDev = foundDev;
            Console.WriteLine($"ath5k: found device VID:0x{PciDev.VendorID:X4} DID:0x{PciDev.DeviceID:X4} at {PciDev.bus}:{PciDev.slot}:{PciDev.function}");

            // Enable Memory and Bus Master in PCI Command Register
            PciDev.EnableMemory(true);
            PciDev.EnableBusMaster(true);

            // Set PCI latency timer and cache line size for DMA
            PciDev.WriteRegister8(0x0C, 16); // Cache line size = 16 dwords (64B)
            PciDev.WriteRegister8(0x0D, 0xA8); // Latency timer
            PciDev.WriteRegister8(0x41, 0); // Disable retry timeout

            // Read BAR0 (MMIO base)
            uint bar0 = PciDev.ReadRegister32(0x10);
            MmioBase = bar0 & 0xFFFFFFF0;
            if (MmioBase == 0)
            {
                Console.WriteLine("ath5k: BAR0 MMIO base is 0!");
                HardwareSummary = "BAR0 MMIO Invalido";
                return false;
            }

            Console.WriteLine($"ath5k: MMIO base mapped at 0x{MmioBase:X8}");
            Mmio = new MemoryBlock(MmioBase, 0x10000);

            // Program PCIe SerDes if PCIe device (e.g. AR2425 DID 0x001C)
            if (PciDev.DeviceID == 0x001C || PciDev.DeviceID == 0x0023 || PciDev.DeviceID == 0x0024)
            {
                Console.WriteLine("ath5k: configuring PCIe SerDes PLL and power settings...");
                WriteReg(AR5K_PCIE_SERDES, 0x9248fc00);
                WriteReg(AR5K_PCIE_SERDES, 0x24924924);
                WriteReg(AR5K_PCIE_SERDES, 0x28000039);
                WriteReg(AR5K_PCIE_SERDES, 0x53160824);
                WriteReg(AR5K_PCIE_SERDES, 0xe5980579);
                WriteReg(AR5K_PCIE_SERDES, 0x001defff);
                WriteReg(AR5K_PCIE_SERDES, 0x1aaabe40);
                WriteReg(AR5K_PCIE_SERDES, 0xbe105554);
                WriteReg(AR5K_PCIE_SERDES, 0x000e3007);
                WriteReg(AR5K_PCIE_SERDES_RESET, 0);
                DelayMicroseconds(1000);
            }

            // Wake up NIC & soft reset
            NicWakeup();

            // Read SREV & PHY IDs
            MacSrev = ReadReg(0x0020); // AR5K_SREV
            PhyChipId = ReadReg(AR5K_PHY_CHIP_ID);
            Console.WriteLine($"ath5k: MAC SREV: 0x{MacSrev:X8}, PHY ID: 0x{PhyChipId:X8}");

            // Read EEPROM Header and MAC
            InitEeprom();

            HardwareSummary = $"Atheros AR2425/AR5212 [PCI {PciDev.bus}:{PciDev.slot}:{PciDev.function}] MAC {GetMacString()} SREV 0x{MacSrev:X2}";

            // Allocate and setup RX / TX DMA Descriptors
            SetupDmaDescriptors();

            // Reset and tune to Channel 1 (2412 MHz)
            ResetAndTuneChannel(1);

            IsInitialized = true;
            Console.WriteLine("ath5k: Atheros 802.11 driver initialized and actively listening for Beacons!");
            return true;
        }

        private void WriteReg(uint reg, uint val)
        {
            Mmio.DWords[reg] = val;
        }

        private uint ReadReg(uint reg)
        {
            return Mmio.DWords[reg];
        }

        private void DelayMicroseconds(int us)
        {
            // Intel Atom N270 calibration loop
            int count = us * 12;
            for (int i = 0; i < count; i++)
            {
                IOPort.Wait();
            }
        }

        private void NicWakeup()
        {
            // Clear power save bit in STA_ID1
            uint sta1 = ReadReg(AR5K_STA_ID1);
            WriteReg(AR5K_STA_ID1, sta1 & ~0x00040000u);

            // Set wake in SLEEP_CTL
            WriteReg(AR5K_SLEEP_CTL, 0);

            // Poll PCICFG until sleep state clears
            for (int i = 0; i < 5000; i++)
            {
                if ((ReadReg(AR5K_PCICFG) & 0x00010000u) == 0) break;
                DelayMicroseconds(10);
            }

            // Warm reset: reset baseband and PCU (Do NOT reset PCI on PCIe!)
            WriteReg(AR5K_RESET_CTL, 0x03);
            DelayMicroseconds(50);
            WriteReg(AR5K_SLEEP_CTL, 0);
            WriteReg(AR5K_RESET_CTL, 0);
            WriteReg(AR5K_CFG, 0);
            DelayMicroseconds(100);
        }

        private void InitEeprom()
        {
            // Read magic & version
            ushort magic = ReadEepromWord(0x003D);
            EepromVersion = ReadEepromWord(0x00C1);
            EepromRegDomain = ReadEepromWord(0x00BF);

            // Read MAC address from offsets 0x1F, 0x1E, 0x1D
            ushort w2 = ReadEepromWord(0x1F);
            ushort w1 = ReadEepromWord(0x1E);
            ushort w0 = ReadEepromWord(0x1D);

            MacAddress[0] = (byte)(w2 & 0xFF);
            MacAddress[1] = (byte)((w2 >> 8) & 0xFF);
            MacAddress[2] = (byte)(w1 & 0xFF);
            MacAddress[3] = (byte)((w1 >> 8) & 0xFF);
            MacAddress[4] = (byte)(w0 & 0xFF);
            MacAddress[5] = (byte)((w0 >> 8) & 0xFF);

            // If EEPROM read was blank (00:00:00:00:00:00 or FF:FF:FF:FF:FF:FF), use Atheros fallback
            if ((MacAddress[0] == 0 && MacAddress[1] == 0 && MacAddress[2] == 0) ||
                (MacAddress[0] == 0xFF && MacAddress[1] == 0xFF))
            {
                MacAddress = new byte[] { 0x00, 0x1E, 0x65, 0x8F, 0xC1, 0x0D };
            }

            Console.WriteLine($"ath5k: EEPROM Ver: 0x{EepromVersion:X4}, RegDomain: 0x{EepromRegDomain:X4}, MAC: {GetMacString()}");
        }

        private ushort ReadEepromWord(uint offset)
        {
            WriteReg(AR5K_EEPROM_BASE, offset);
            WriteReg(AR5K_EEPROM_CMD, 0x01); // Read command

            for (int i = 0; i < 2000; i++)
            {
                uint stat = ReadReg(AR5K_EEPROM_STAT_5211);
                if ((stat & 0x02) != 0) // Done
                {
                    return (ushort)(ReadReg(AR5K_EEPROM_DATA_5211) & 0xFFFF);
                }
                DelayMicroseconds(5);
            }
            return 0;
        }

        public string GetMacString()
        {
            return $"{MacAddress[0]:X2}:{MacAddress[1]:X2}:{MacAddress[2]:X2}:{MacAddress[3]:X2}:{MacAddress[4]:X2}:{MacAddress[5]:X2}";
        }

        private void SetupDmaDescriptors()
        {
            // Allocate 16 RX Descriptors (24 bytes each, aligned) + Buffers (2048 bytes each)
            int descTotalSize = RX_DESC_COUNT * 32;
            int bufTotalSize = RX_DESC_COUNT * RX_BUF_SIZE;

            _rxDescMem = new ManagedMemoryBlock((uint)descTotalSize, 256);
            _rxDescPhys = (uint)_rxDescMem.Offset;

            _rxBufferMem = new ManagedMemoryBlock((uint)bufTotalSize, 8);
            _rxBufferPhys = (uint)_rxBufferMem.Offset;

            // Initialize RX Descriptors Ring
            for (int i = 0; i < RX_DESC_COUNT; i++)
            {
                uint dAddr = (uint)(_rxDescPhys + i * 32);
                uint nextDAddr = (uint)(_rxDescPhys + ((i + 1) % RX_DESC_COUNT) * 32);
                uint bAddr = (uint)(_rxBufferPhys + i * RX_BUF_SIZE);

                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                descBlock.DWords[0] = nextDAddr; // ds_link
                descBlock.DWords[4] = bAddr;    // ds_data
                descBlock.DWords[8] = 0;        // rx_ctl0
                descBlock.DWords[12] = (uint)(RX_BUF_SIZE & 0x0FFF); // rx_ctl1: buf_len
                descBlock.DWords[16] = 0;       // rx_stat0
                descBlock.DWords[20] = 0;       // rx_stat1
            }

            // Allocate TX Descriptors & Buffers
            int txDescTotal = TX_DESC_COUNT * 32;
            int txBufTotal = TX_DESC_COUNT * TX_BUF_SIZE;

            _txDescMem = new ManagedMemoryBlock((uint)txDescTotal, 256);
            _txDescPhys = (uint)_txDescMem.Offset;

            _txBufferMem = new ManagedMemoryBlock((uint)txBufTotal, 8);
            _txBufferPhys = (uint)_txBufferMem.Offset;

            for (int i = 0; i < TX_DESC_COUNT; i++)
            {
                uint dAddr = (uint)(_txDescPhys + i * 32);
                uint bAddr = (uint)(_txBufferPhys + i * TX_BUF_SIZE);

                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                descBlock.DWords[0] = 0;        // ds_link
                descBlock.DWords[4] = bAddr;    // ds_data
                descBlock.DWords[8] = 0;        // tx_ctl0
                descBlock.DWords[12] = 0;       // tx_ctl1
                descBlock.DWords[16] = 0;       // tx_ctl2
                descBlock.DWords[20] = 0;       // tx_ctl3
                descBlock.DWords[24] = 0;       // tx_stat0
                descBlock.DWords[28] = 0;       // tx_stat1
            }
        }

        public void ResetAndTuneChannel(int channel)
        {
            if (channel < 1) channel = 1;
            if (channel > 13) channel = 13;
            CurrentChannel = channel;

            // Stop RX & TX DMA
            WriteReg(AR5K_CR, AR5K_CR_RXD | AR5K_CR_TXD);
            WriteReg(AR5K_IER, 0); // Polling mode only!

            NicWakeup();

            // Set PHY Shift
            WriteReg(AR5K_PHY_0, 0x00000007); // AR5K_PHY_SHIFT_5GHZ

            // Apply Mode Start Initvals
            ApplyModeStartTable();

            // Apply Common Start Initvals
            ApplyCommonStartTable();

            // Apply Mode End (RF2425)
            ApplyModeEndTable();

            // Apply Common End (RF2425)
            ApplyCommonEndTable();

            // Baseband gain & RF gain
            ApplyBbGainTable();
            ApplyRfGainTable();

            // Tweak initval settings for AR2425 Swan
            WriteReg(AR5K_PHY_SCAL, 0x0E);
            WriteReg(AR5K_PHY_FAST_ADC, 0x01);

            // Commit RF Buffer
            ApplyRfBuffer();

            // OFDM Timings (AR5K_PHY_TIMING_3)
            WriteReg(AR5K_PHY_TIMING_3, 0x9C0A9F6B);

            // Antenna switch table & idle control (Both antennas: Main + Aux)
            WriteReg(AR5K_PHY_ANT_SWITCH_TABLE_0, 0x00000001);
            WriteReg(AR5K_PHY_ANT_SWITCH_TABLE_1, 0x00000002);
            WriteReg(AR5K_PHY_ANT_CTL, 0x00000011); // Diversity active on both antennas
            WriteReg(AR5K_PHY_NFTHRES, 0x000001F0); // Noise floor threshold (-95 dBm)
            WriteReg(AR5K_DEFAULT_ANTENNA, 0x01);   // Default to Antenna 1 (Main / Cabo Preto)

            // Setup PLL & Channel Frequency
            // Clock = 44MHz (0xAB) | PLL_RF5112 (0x40) = 0xEB
            WriteReg(AR5K_PHY_PLL, 0x000000EB);
            DelayMicroseconds(300);

            // PHY Mode: Dynamic OFDM/CCK (0x04) | 2GHz (0x02) | RF5112 (0x08) = 0x0E
            WriteReg(AR5K_PHY_MODE, 0x0000000E);
            WriteReg(AR5K_PHY_TURBO, 0);

            // Tune Frequency: Freq = 2407 + 5 * channel
            int freqMhz = 2407 + 5 * channel;
            TuneRf2425Channel(freqMhz);

            // Activate PHY
            WriteReg(AR5K_PHY_ACT, 0x01);
            DelayMicroseconds(500);

            // Run AGC Calibration
            uint agc = ReadReg(AR5K_PHY_AGCCTL);
            WriteReg(AR5K_PHY_AGCCTL, agc | 0x03); // Calibrate AGC & NF
            for (int i = 0; i < 1000; i++)
            {
                if ((ReadReg(AR5K_PHY_AGCCTL) & 0x03) == 0) break;
                DelayMicroseconds(10);
            }

            // Setup MAC addresses & BSSID filter
            uint lowMac = (uint)(MacAddress[0] | (MacAddress[1] << 8) | (MacAddress[2] << 16) | (MacAddress[3] << 24));
            uint highMac = (uint)(MacAddress[4] | (MacAddress[5] << 8));
            WriteReg(AR5K_STA_ID0, lowMac);
            WriteReg(AR5K_STA_ID1, highMac | 0x00000000); // Station mode

            // BSSID Mask (Promiscuous BSSID acceptance for scanning)
            WriteReg(AR5K_BSS_IDM0, 0x00000000);
            WriteReg(AR5K_BSS_IDM1, 0x00000000);
            WriteReg(AR5K_BSS_ID0, 0xFFFFFFFF);
            WriteReg(AR5K_BSS_ID1, 0x0000FFFF);

            // Multicast filter accept all
            WriteReg(AR5K_MCAST_FILTER0, 0xFFFFFFFF);
            WriteReg(AR5K_MCAST_FILTER1, 0xFFFFFFFF);

            // RX Filter: Accept Unicast, Broadcast, Multicast, Control, Beacons, Probe Requests, Promiscuous
            WriteReg(AR5K_RX_FILTER, 0x000000FF);
            WriteReg(AR5K_DIAG_SW_5211, 0); // Ensure RX is NOT disabled

            // DMA Config
            WriteReg(AR5K_TXCFG, 0x00000015); // 128-byte DMA bursts
            WriteReg(AR5K_RXCFG, 0x00000005); // 128-byte DMA bursts

            // Setup TX Queue 0 for active Probe Requests
            WriteReg(AR5K_QUEUE_QCUMASK_0, 0x01);
            WriteReg(AR5K_QUEUE_DFS_LOCAL_IFS_0, 0x002FFC0F);
            WriteReg(AR5K_QUEUE_DFS_RETRY_LIMIT_0, 0x0000000A);
            WriteReg(AR5K_QUEUE_MISC_0, 0);

            // Start RX DMA
            WriteReg(AR5K_RXDP, _rxDescPhys);
            WriteReg(AR5K_CR, AR5K_CR_RXE);
        }

        private void TuneRf2425Channel(int freqMhz)
        {
            // Reverse bits of (freq - 2272)
            int c = freqMhz - 2272;
            byte b = (byte)c;
            byte swapped = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((b & (1 << i)) != 0) swapped |= (byte)(1 << (7 - i));
            }

            uint data = (uint)((swapped << 4) | 0x1001);
            WriteReg(AR5K_RF_BUFFER, data & 0xFF);
            WriteReg(AR5K_RF_BUFFER_CONTROL_5, (data >> 8) & 0x7F);
            WriteReg(AR5K_PHY_CCKTXCTL, 0);
        }

        private void ApplyModeStartTable()
        {
            uint[] t = Ar5212IniModeStart;
            for (int i = 0; i < t.Length; i += 3)
            {
                WriteReg(t[i], t[i + 2]); // column 2 = 11g
            }
        }

        private void ApplyCommonStartTable()
        {
            uint[] t = Ar5212IniCommonStart;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyModeEndTable()
        {
            uint[] t = Rf2425IniModeEnd;
            for (int i = 0; i < t.Length; i += 3)
            {
                WriteReg(t[i], t[i + 2]); // column 2 = 11g
            }
        }

        private void ApplyCommonEndTable()
        {
            uint[] t = Rf2425IniCommonEnd;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyBbGainTable()
        {
            uint[] t = Rf5112IniBbGain;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyRfGainTable()
        {
            uint[] t = RfGain2425;
            for (int i = 0; i < t.Length; i += 2)
            {
                WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyRfBuffer()
        {
            uint[] rfb = Rfb2425;
            for (int i = 0; i < rfb.Length; i += 4)
            {
                uint regAddr = rfb[i + 1];
                uint val = rfb[i + 3]; // 11g value
                WriteReg(regAddr, val);
                DelayMicroseconds(2);
            }
        }

        /// <summary>
        /// Polls the RX descriptor ring for received 802.11 frames.
        /// </summary>
        public void PollRx()
        {
            if (!IsInitialized) return;

            for (int step = 0; step < RX_DESC_COUNT; step++)
            {
                uint dAddr = (uint)(_rxDescPhys + _rxHead * 32);
                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                uint stat1 = descBlock.DWords[20];

                // Check AR5K_5212_RX_DESC_STATUS1_DONE (Bit 0)
                if ((stat1 & 0x01) == 0)
                {
                    break; // No new packet at this descriptor
                }

                // AR5K_5212_RX_DESC_STATUS1_FRAME_RECEIVE_OK is Bit 1 (0x02)
                // Filter out bad packets (CRC error 0x04, Decrypt CRC 0x08, PHY error 0x10, MIC error 0x20)
                bool rxOk = (stat1 & 0x02) != 0;
                bool hasErr = (stat1 & 0x3C) != 0;

                if (rxOk && !hasErr)
                {
                    uint stat0 = descBlock.DWords[16];
                    int frameLen = (int)(stat0 & 0x0FFF);
                    int rawRssi = (int)((stat0 >> 20) & 0xFF);
                    int rssiDbm = rawRssi > 0 ? (rawRssi - 95) : -95;
                    if (rssiDbm > -10) rssiDbm = -40; // clamp
                    int antenna = (int)((stat0 >> 28) & 0x07);

                    if (frameLen > 24 && frameLen < RX_BUF_SIZE)
                    {
                        uint bAddr = (uint)(_rxBufferPhys + _rxHead * RX_BUF_SIZE);
                        MemoryBlock bufBlock = new MemoryBlock(bAddr, (uint)frameLen);
                        byte[] packet = new byte[frameLen];
                        for (int b = 0; b < frameLen; b++)
                        {
                            packet[b] = bufBlock.Bytes[(uint)b];
                        }

                        RxPacketsCount++;
                        Parse80211Frame(packet, rssiDbm, antenna);
                    }
                }

                // Reset descriptor for reuse
                descBlock.DWords[16] = 0; // stat0 = 0
                descBlock.DWords[20] = 0; // stat1 = 0
                descBlock.DWords[8] = 0;  // ctl0 = 0
                descBlock.DWords[12] = (uint)(RX_BUF_SIZE & 0x0FFF); // ctl1

                _rxHead = (_rxHead + 1) % RX_DESC_COUNT;
            }

            // Ensure RX engine remains active
            if ((ReadReg(AR5K_CR) & AR5K_CR_RXE) == 0)
            {
                WriteReg(AR5K_CR, AR5K_CR_RXE);
            }
        }

        private void Parse80211Frame(byte[] pkt, int rssiDbm, int antenna)
        {
            if (pkt.Length < 24) return;

            byte frameControl = pkt[0];
            byte frameType = (byte)(frameControl & 0xFC);

            // Management frames:
            // 0x80 = Beacon Frame
            // 0x50 = Probe Response
            if (frameType == 0x80 || frameType == 0x50)
            {
                if (frameType == 0x80) BeaconFramesCount++;

                // 802.11 Header:
                // Byte 4..9: Dest Address (Broadcast FF:FF:FF:FF:FF:FF)
                // Byte 10..15: Source Address (BSSID)
                // Byte 16..21: BSSID
                string bssid = $"{pkt[16]:X2}:{pkt[17]:X2}:{pkt[18]:X2}:{pkt[19]:X2}:{pkt[20]:X2}:{pkt[21]:X2}";

                // Management Frame Body starts at offset 24:
                // Offset 24..31: Timestamp (8 bytes)
                // Offset 32..33: Beacon Interval (2 bytes)
                // Offset 34..35: Capability Information (2 bytes)
                if (pkt.Length < 36) return;

                ushort capInfo = (ushort)(pkt[34] | (pkt[35] << 8));
                bool privacy = (capInfo & 0x0010) != 0;

                // Information Elements (IEs) start at offset 36
                int offset = 36;
                string ssid = "";
                int channel = CurrentChannel;
                string security = privacy ? "WPA/WPA2" : "OPEN";
                string cipher = privacy ? "CCMP (AES)" : "None (Clear)";
                string authType = privacy ? "PSK" : "Open System";

                while (offset + 2 <= pkt.Length)
                {
                    byte ieId = pkt[offset];
                    byte ieLen = pkt[offset + 1];
                    offset += 2;

                    if (offset + ieLen > pkt.Length) break;

                    if (ieId == 0) // SSID
                    {
                        if (ieLen > 0 && ieLen <= 32)
                        {
                            char[] chars = new char[ieLen];
                            for (int c = 0; c < ieLen; c++)
                            {
                                byte cb = pkt[offset + c];
                                chars[c] = (cb >= 32 && cb <= 126) ? (char)cb : '?';
                            }
                            ssid = new string(chars);
                        }
                        else
                        {
                            ssid = "<Hidden Network>";
                        }
                    }
                    else if (ieId == 3 && ieLen >= 1) // DS Parameter Set (Current Channel)
                    {
                        channel = pkt[offset];
                    }
                    else if (ieId == 48) // RSN (WPA2)
                    {
                        security = "WPA2-PSK";
                        cipher = "CCMP (AES-128)";
                        authType = "PSK (Pre-Shared Key)";
                    }
                    else if (ieId == 221 && ieLen >= 4) // Vendor Specific (WPA1 / WPS / WMM)
                    {
                        if (pkt[offset] == 0x00 && pkt[offset + 1] == 0x50 && pkt[offset + 2] == 0xF2 && pkt[offset + 3] == 0x01)
                        {
                            if (security == "OPEN" || security == "WPA/WPA2")
                            {
                                security = "WPA-PSK";
                                cipher = "TKIP";
                            }
                        }
                    }

                    offset += ieLen;
                }

                if (string.IsNullOrEmpty(ssid) || ssid == "<Hidden Network>" || ssid.Trim().Length == 0) return;

                // Validate channel
                if (channel < 1 || channel > 13) channel = CurrentChannel;

                // Add or update AP in DetectedAps
                if (!DetectedAps.TryGetValue(bssid, out var ap))
                {
                    ap = new WifiAccessPoint();
                    ap.Bssid = bssid;
                    DetectedAps[bssid] = ap;
                }

                ap.Ssid = ssid;
                ap.Channel = channel;
                ap.FrequencyMHz = 2407 + 5 * channel;
                ap.RssiDbm = rssiDbm;
                ap.NoiseDbm = -95;
                ap.Antenna1Dbm = antenna == 1 ? rssiDbm : rssiDbm - 1;
                ap.Antenna2Dbm = antenna == 2 ? rssiDbm : rssiDbm - 2;
                ap.Security = security;
                ap.Cipher = cipher;
                ap.AuthType = authType;
                ap.PhyMode = "802.11b/g";
                ap.MaxRateMbps = 54;
                ap.IsOpen = !privacy;
                ap.Capabilities = $"ESS, {(privacy ? "Privacy, " : "")}ShortPreamble";
            }
            else if (frameType == 0x10) // Association Response
            {
                if (pkt.Length >= 28)
                {
                    ushort status = (ushort)(pkt[26] | (pkt[27] << 8));
                    if (status == 0)
                    {
                        LastAssocSuccess = true;
                    }
                }
            }
            else if (frameType == 0xC0 || frameType == 0xA0) // Deauthentication or Disassociation
            {
                if (Wpa2Engine != null && Wpa2Engine.State != Wpa2HandshakeState.Idle && Wpa2Engine.State != Wpa2HandshakeState.Failed)
                {
                    ushort reason = pkt.Length >= 26 ? (ushort)(pkt[24] | (pkt[25] << 8)) : (ushort)0;
                    Wpa2Engine.State = Wpa2HandshakeState.Failed;
                    Wpa2Engine.FailureReason = "AP Deauthenticated/Disconnected station (Code " + reason + ")";
                }
            }
            else if ((frameControl & 0x0C) == 0x08) // Data or QoS Data frame
            {
                // Check for 802.1X EAPOL (LLC/SNAP: AA AA 03 00 00 00 88 8E)
                // Offset can be 26 (QoS Data) or 24 (Regular Data)
                int snapOffset = ((frameControl & 0xF0) == 0x80) ? 26 : 24;
                if (pkt.Length < snapOffset + 8 && pkt.Length >= 32)
                {
                    // Fallback check
                    snapOffset = 24;
                }

                if (pkt.Length >= snapOffset + 8)
                {
                    if (pkt[snapOffset] == 0xAA && pkt[snapOffset + 1] == 0xAA &&
                        pkt[snapOffset + 2] == 0x03 && pkt[snapOffset + 3] == 0x00 &&
                        pkt[snapOffset + 4] == 0x00 && pkt[snapOffset + 5] == 0x00 &&
                        pkt[snapOffset + 6] == 0x88 && pkt[snapOffset + 7] == 0x8E)
                    {
                        int eapolOffset = snapOffset + 8;
                        if (Wpa2Engine != null && Wpa2Engine.State != Wpa2HandshakeState.Failed && Wpa2Engine.State != Wpa2HandshakeState.Completed)
                        {
                            uint tsfL = ReadReg(AR5K_TSF_L32);
                            uint tsfU = ReadReg(AR5K_TSF_U32);

                            byte[] respFrame;
                            bool processed = Wpa2Engine.ProcessEapolFrame(pkt, eapolOffset, tsfL, tsfU, out respFrame);
                            if (processed && respFrame != null)
                            {
                                SendRawFrame(respFrame, true);

                                if (Wpa2Engine.State == Wpa2HandshakeState.Completed)
                                {
                                    // Hardware key installation (CCMP AES-128 into AR5212 Key Table entry 0)
                                    SetHardwareKey(0, AR5K_KEYTABLE_TYPE_CCM, Wpa2Engine.ApBssid, Wpa2Engine.Tk);
                                    LastAssocSuccess = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        public volatile bool LastAssocSuccess = false;

        public void SetHardwareKey(int entry, uint cipherType, byte[] mac, byte[] key)
        {
            if (entry < 0 || entry >= 128) return;
            uint baseReg = 0x8800 + (uint)(entry << 5);

            for (int i = 0; i < 4; i++)
            {
                uint kw = 0;
                if (key != null && key.Length >= (i + 1) * 4)
                {
                    kw = (uint)(key[i * 4] | (key[i * 4 + 1] << 8) | (key[i * 4 + 2] << 16) | (key[i * 4 + 3] << 24));
                }
                WriteReg(baseReg + (uint)(i * 4), kw);
            }

            WriteReg(baseReg + 16, 0); // Reserved / TX MIC
            WriteReg(baseReg + 20, cipherType); // CCM = 6

            uint mac0 = 0;
            if (mac != null && mac.Length >= 4)
            {
                mac0 = (uint)(mac[0] | (mac[1] << 8) | (mac[2] << 16) | (mac[3] << 24));
            }
            WriteReg(baseReg + 24, mac0);

            uint mac1 = AR5K_KEYTABLE_VALID;
            if (mac != null && mac.Length >= 6)
            {
                mac1 |= (uint)(mac[4] | (mac[5] << 8));
            }
            WriteReg(baseReg + 28, mac1);
        }

        public void ResetHardwareKey(int entry)
        {
            if (entry < 0 || entry >= 128) return;
            uint baseReg = 0x8800 + (uint)(entry << 5);
            for (int i = 0; i < 8; i++)
            {
                WriteReg(baseReg + (uint)(i * 4), 0);
            }
            WriteReg(baseReg + 20, AR5K_KEYTABLE_TYPE_NULL);
        }

        public void SendRawFrame(byte[] frame, bool isUnicast)
        {
            if (!IsInitialized || frame == null || frame.Length == 0) return;

            // Write frame to TX buffer
            uint bAddr = (uint)(_txBufferPhys + _txHead * TX_BUF_SIZE);
            MemoryBlock bufBlock = new MemoryBlock(bAddr, (uint)frame.Length);
            for (int i = 0; i < frame.Length; i++)
            {
                bufBlock.Bytes[(uint)i] = frame[i];
            }

            // Setup 5212 4-word TX Descriptor
            uint dAddr = (uint)(_txDescPhys + _txHead * 32);
            MemoryBlock descBlock = new MemoryBlock(dAddr, 32);

            descBlock.DWords[0] = 0;     // ds_link = 0
            descBlock.DWords[4] = bAddr; // ds_data
            
            // Frame length in tx_ctl0 includes 4 bytes of hardware-generated FCS
            uint frameLenWithFcs = (uint)((frame.Length + 4) & 0x0FFF);
            // tx_ctl0: frame_len | xmit_power(60) | clrdmask(0x01000000)
            descBlock.DWords[8] = frameLenWithFcs | (60u << 16) | 0x01000000u;
            
            // tx_ctl1: buf_len | (isUnicast ? 0 : NOACK 0x01000000)
            uint noAckFlag = isUnicast ? 0u : 0x01000000u;
            descBlock.DWords[12] = (uint)(frame.Length & 0x0FFF) | noAckFlag;
            
            // tx_ctl2: xmit_tries0 = 4 hardware retries (bits 16-19: 0x00040000)
            descBlock.DWords[16] = 0x00040000u;
            
            // tx_ctl3: xmit_rate0 = 0x1B (1 Mbps CCK for 802.11 management frames)
            descBlock.DWords[20] = 0x1B;
            descBlock.DWords[24] = 0;    // tx_stat0
            descBlock.DWords[28] = 0;    // tx_stat1

            // Trigger TX DMA on Queue 0
            WriteReg(AR5K_QUEUE_TXDP_0, dAddr);
            WriteReg(AR5K_QCU_TXE, 0x01);
            TxPacketsCount++;

            _txHead = (_txHead + 1) % TX_DESC_COUNT;
        }

        /// <summary>
        /// Sends an active 802.11 Broadcast Probe Request frame on the current channel.
        /// </summary>
        public void SendProbeRequest()
        {
            if (!IsInitialized) return;

            byte[] frame = new byte[] {
                0x40, 0x00, // Frame Control (Probe Request)
                0x00, 0x00, // Duration
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // DA (Broadcast)
                MacAddress[0], MacAddress[1], MacAddress[2], MacAddress[3], MacAddress[4], MacAddress[5], // SA
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // BSSID
                0x00, 0x00, // Sequence Control
                0x00, 0x00, // SSID IE (Length 0 = Wildcard)
                0x01, 0x08, 0x82, 0x84, 0x8B, 0x96, 0x0C, 0x12, 0x18, 0x24 // Supported Rates
            };

            SendRawFrame(frame, false);
        }

        public void SendAuthFrame(byte[] bssid)
        {
            byte[] frame = new byte[30];
            frame[0] = 0xB0; // Management: Authentication
            frame[1] = 0x00; // Flags
            frame[2] = 0x3A; // Duration 314us
            frame[3] = 0x01;
            // DA: AP BSSID
            frame[4] = bssid[0]; frame[5] = bssid[1]; frame[6] = bssid[2];
            frame[7] = bssid[3]; frame[8] = bssid[4]; frame[9] = bssid[5];
            // SA: Station MAC
            frame[10] = MacAddress[0]; frame[11] = MacAddress[1]; frame[12] = MacAddress[2];
            frame[13] = MacAddress[3]; frame[14] = MacAddress[4]; frame[15] = MacAddress[5];
            // BSSID: AP BSSID
            frame[16] = bssid[0]; frame[17] = bssid[1]; frame[18] = bssid[2];
            frame[19] = bssid[3]; frame[20] = bssid[4]; frame[21] = bssid[5];
            // Seq Ctrl
            frame[22] = 0x00; frame[23] = 0x00;
            // Auth Body
            frame[24] = 0x00; frame[25] = 0x00; // Open System (0)
            frame[26] = 0x01; frame[27] = 0x00; // Sequence 1
            frame[28] = 0x00; frame[29] = 0x00; // Status 0 (Success)

            SendRawFrame(frame, true);
        }

        public void SendAssocRequestFrame(byte[] bssid, string ssid, bool isWpa2 = false)
        {
            byte[] ssidBytes = System.Text.Encoding.ASCII.GetBytes(ssid ?? "");
            int sLen = ssidBytes.Length;
            if (sLen > 32) sLen = 32;

            int rsnLen = isWpa2 ? 22 : 0;
            int totalLen = 24 + 4 + 2 + sLen + 10 + 6 + rsnLen;
            byte[] frame = new byte[totalLen];
            frame[0] = 0x00; // Management: Association Request
            frame[1] = 0x00;
            frame[2] = 0x3A; // Duration 314us
            frame[3] = 0x01;
            // DA
            frame[4] = bssid[0]; frame[5] = bssid[1]; frame[6] = bssid[2];
            frame[7] = bssid[3]; frame[8] = bssid[4]; frame[9] = bssid[5];
            // SA
            frame[10] = MacAddress[0]; frame[11] = MacAddress[1]; frame[12] = MacAddress[2];
            frame[13] = MacAddress[3]; frame[14] = MacAddress[4]; frame[15] = MacAddress[5];
            // BSSID
            frame[16] = bssid[0]; frame[17] = bssid[1]; frame[18] = bssid[2];
            frame[19] = bssid[3]; frame[20] = bssid[4]; frame[21] = bssid[5];
            // Seq Ctrl
            frame[22] = 0x10; frame[23] = 0x00;

            // Cap Info (0x0411: ESS, Short Preamble, Short Slot Time; + 0x0010 Privacy if WPA2)
            frame[24] = isWpa2 ? (byte)0x31 : (byte)0x21;
            frame[25] = 0x04;
            // Listen Interval
            frame[26] = 0x0A; frame[27] = 0x00;

            int pos = 28;
            // Tag 0: SSID
            frame[pos++] = 0x00;
            frame[pos++] = (byte)sLen;
            for (int i = 0; i < sLen; i++) frame[pos++] = ssidBytes[i];

            // Tag 1: Supported Rates (1, 2, 5.5, 11, 6, 9, 12, 18 Mbps)
            frame[pos++] = 0x01;
            frame[pos++] = 0x08;
            frame[pos++] = 0x82; frame[pos++] = 0x84; frame[pos++] = 0x8B; frame[pos++] = 0x96;
            frame[pos++] = 0x0C; frame[pos++] = 0x12; frame[pos++] = 0x18; frame[pos++] = 0x24;

            // Tag 50: Extended Supported Rates (24, 36, 48, 54 Mbps)
            frame[pos++] = 0x32;
            frame[pos++] = 0x04;
            frame[pos++] = 0x30; frame[pos++] = 0x48; frame[pos++] = 0x60; frame[pos++] = 0x6C;

            // Tag 48: RSN Information Element (WPA2)
            if (isWpa2)
            {
                frame[pos++] = 0x30;
                frame[pos++] = 0x14;
                frame[pos++] = 0x01; frame[pos++] = 0x00;                         // Version 1
                frame[pos++] = 0x00; frame[pos++] = 0x0F; frame[pos++] = 0xAC; frame[pos++] = 0x04; // Group: AES-CCMP
                frame[pos++] = 0x01; frame[pos++] = 0x00;                         // Pairwise count: 1
                frame[pos++] = 0x00; frame[pos++] = 0x0F; frame[pos++] = 0xAC; frame[pos++] = 0x04; // Pairwise: AES-CCMP
                frame[pos++] = 0x01; frame[pos++] = 0x00;                         // AKM count: 1
                frame[pos++] = 0x00; frame[pos++] = 0x0F; frame[pos++] = 0xAC; frame[pos++] = 0x02; // AKM: PSK
                frame[pos++] = 0x00; frame[pos++] = 0x00;                         // Capabilities
            }

            SendRawFrame(frame, true);
        }

        public bool AssociateWithAp(string bssidStr, string ssid, int channel)
        {
            if (!IsInitialized) return false;

            byte[] bssid = ParseMacFast(bssidStr);
            LastAssocSuccess = false;

            // Tune to AP channel
            ResetAndTuneChannel(channel);
            DelayMicroseconds(25000); // 25ms PLL lock

            // Transmit Authentication Frame
            SendAuthFrame(bssid);

            for (int i = 0; i < 3000; i++)
            {
                PollRx();
                DelayMicroseconds(10);
            }

            // Transmit Association Request Frame (Open network)
            SendAssocRequestFrame(bssid, ssid, false);

            for (int i = 0; i < 5000; i++)
            {
                PollRx();
                DelayMicroseconds(10);
            }

            return true;
        }

        public void StartWpa2Handshake(string bssidStr, string ssid, int channel, string passphrase)
        {
            if (!IsInitialized) return;

            byte[] bssid = ParseMacFast(bssidStr);
            LastAssocSuccess = false;

            // Tune to AP channel
            ResetAndTuneChannel(channel);
            DelayMicroseconds(25000); // 25ms PLL lock

            // Reset hardware key table
            ResetHardwareKey(0);

            // Initialize WPA2 handshake state machine
            Wpa2Engine = new Wpa2HandshakeEngine();
            Wpa2Engine.Initialize(bssid, MacAddress, ssid, passphrase, channel);

            // Transmit 802.11 Open Authentication
            SendAuthFrame(bssid);

            for (int i = 0; i < 2000; i++)
            {
                PollRx();
                DelayMicroseconds(10);
            }

            // Transmit 802.11 Association Request with WPA2 RSN IE
            SendAssocRequestFrame(bssid, ssid, true);

            Wpa2Engine.State = Wpa2HandshakeState.WaitingMsg1;
        }

        public static byte[] ParseMacFast(string macStr)
        {
            byte[] mac = new byte[6];
            if (macStr == null || macStr.Length < 17) return mac;
            int idx = 0;
            for (int i = 0; i < 6; i++)
            {
                int hi = HexNibbleVal(macStr[idx++]);
                int lo = HexNibbleVal(macStr[idx++]);
                mac[i] = (byte)((hi << 4) | lo);
                if (idx < macStr.Length && macStr[idx] == ':') idx++;
            }
            return mac;
        }

        private static int HexNibbleVal(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return 0;
        }

        /// <summary>
        /// Sweeps all 13 2.4GHz channels, transmits active Probe Requests, and listens for Beacons.
        /// </summary>
        public void ScanAllChannels()
        {
            if (!IsInitialized) return;

            for (int ch = 1; ch <= 13; ch++)
            {
                ResetAndTuneChannel(ch);
                SendProbeRequest();

                // Dwell on channel for 35 ms listening for Beacons & Probe Responses
                for (int d = 0; d < 3500; d++)
                {
                    PollRx();
                    DelayMicroseconds(10);
                }
            }

            // Return to channel 1
            ResetAndTuneChannel(1);
        }

        #region Tables
        private static readonly uint[] Ar5212IniModeStart = new uint[] {
            0x00001040u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001044u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001048u, 0x002FFC1Fu,
            0x002FFC0Fu, 0x0000104Cu, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001050u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001054u,
            0x002FFC1Fu, 0x002FFC0Fu, 0x00001058u, 0x002FFC1Fu, 0x002FFC0Fu, 0x0000105Cu, 0x002FFC1Fu, 0x002FFC0Fu,
            0x00001060u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001064u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001030u, 0x000000B0u,
            0x00000160u, 0x00001070u, 0x000001B8u, 0x0000018Cu, 0x000010B0u, 0x00001F1Cu, 0x00003E38u, 0x000010F0u,
            0x00005880u, 0x0000B0E0u, 0x00008014u, 0x04200420u, 0x08400840u, 0x00009804u, 0x00000000u, 0x00000000u,
            0x00009820u, 0x02010200u, 0x02020200u, 0x00009824u, 0x00000707u, 0x00000E0Eu, 0x00009844u, 0x13721722u,
            0x137216A2u, 0x00009860u, 0x00009D18u, 0x00009D18u, 0x00009864u, 0x0001CE00u, 0x0001CE00u, 0x00009868u,
            0x409A4190u, 0x409A4190u, 0x00009918u, 0x00000084u, 0x00000108u, 0x00009924u, 0x10058A05u, 0x10058A05u,
            0x0000A230u, 0x00000000u, 0x00000108u
        };

        private static readonly uint[] Ar5212IniCommonStart = new uint[] {
            0x0000000Cu, 0x00000000u, 0x00000000u, 0x00000034u, 0x00000005u, 0x00000000u, 0x00000040u, 0x00000000u,
            0x00000000u, 0x00000044u, 0x00000008u, 0x00000000u, 0x00000048u, 0x00000008u, 0x00000000u, 0x0000004Cu,
            0x00000010u, 0x00000000u, 0x00000050u, 0x00000000u, 0x00000000u, 0x00000054u, 0x0000001Fu, 0x00000000u,
            0x00000800u, 0x00000000u, 0x00000000u, 0x00000804u, 0x00000000u, 0x00000000u, 0x00000808u, 0x00000000u,
            0x00000000u, 0x0000080Cu, 0x00000000u, 0x00000000u, 0x00000810u, 0x00000000u, 0x00000000u, 0x00000814u,
            0x00000000u, 0x00000000u, 0x00000818u, 0x00000000u, 0x00000000u, 0x0000081Cu, 0x00000000u, 0x00000000u,
            0x00000820u, 0x00000000u, 0x00000000u, 0x00000824u, 0x00000000u, 0x00000000u, 0x00001230u, 0x00000000u,
            0x00000000u, 0x00001270u, 0x00000000u, 0x00000000u, 0x00001038u, 0x00000000u, 0x00000000u, 0x00001078u,
            0x00000000u, 0x00000000u, 0x000010B8u, 0x00000000u, 0x00000000u, 0x000010F8u, 0x00000000u, 0x00000000u,
            0x00001138u, 0x00000000u, 0x00000000u, 0x00001178u, 0x00000000u, 0x00000000u, 0x000011B8u, 0x00000000u,
            0x00000000u, 0x000011F8u, 0x00000000u, 0x00000000u, 0x00001238u, 0x00000000u, 0x00000000u, 0x00001278u,
            0x00000000u, 0x00000000u, 0x000012B8u, 0x00000000u, 0x00000000u, 0x000012F8u, 0x00000000u, 0x00000000u,
            0x00001338u, 0x00000000u, 0x00000000u, 0x00001378u, 0x00000000u, 0x00000000u, 0x000013B8u, 0x00000000u,
            0x00000000u, 0x000013F8u, 0x00000000u, 0x00000000u, 0x00001438u, 0x00000000u, 0x00000000u, 0x00001478u,
            0x00000000u, 0x00000000u, 0x000014B8u, 0x00000000u, 0x00000000u, 0x000014F8u, 0x00000000u, 0x00000000u,
            0x00001538u, 0x00000000u, 0x00000000u, 0x00001578u, 0x00000000u, 0x00000000u, 0x000015B8u, 0x00000000u,
            0x00000000u, 0x000015F8u, 0x00000000u, 0x00000000u, 0x00001638u, 0x00000000u, 0x00000000u, 0x00001678u,
            0x00000000u, 0x00000000u, 0x000016B8u, 0x00000000u, 0x00000000u, 0x000016F8u, 0x00000000u, 0x00000000u,
            0x00001738u, 0x00000000u, 0x00000000u, 0x00001778u, 0x00000000u, 0x00000000u, 0x000017B8u, 0x00000000u,
            0x00000000u, 0x000017F8u, 0x00000000u, 0x00000000u, 0x0000103Cu, 0x00000000u, 0x00000000u, 0x0000107Cu,
            0x00000000u, 0x00000000u, 0x000010BCu, 0x00000000u, 0x00000000u, 0x000010FCu, 0x00000000u, 0x00000000u,
            0x0000113Cu, 0x00000000u, 0x00000000u, 0x0000117Cu, 0x00000000u, 0x00000000u, 0x000011BCu, 0x00000000u,
            0x00000000u, 0x000011FCu, 0x00000000u, 0x00000000u, 0x0000123Cu, 0x00000000u, 0x00000000u, 0x0000127Cu,
            0x00000000u, 0x00000000u, 0x000012BCu, 0x00000000u, 0x00000000u, 0x000012FCu, 0x00000000u, 0x00000000u,
            0x0000133Cu, 0x00000000u, 0x00000000u, 0x0000137Cu, 0x00000000u, 0x00000000u, 0x000013BCu, 0x00000000u,
            0x00000000u, 0x000013FCu, 0x00000000u, 0x00000000u, 0x0000143Cu, 0x00000000u, 0x00000000u, 0x0000147Cu,
            0x00000000u, 0x00000000u, 0x00008004u, 0x00000000u, 0x00000000u, 0x00008008u, 0x00000000u, 0x00000000u,
            0x0000800Cu, 0x00000000u, 0x00000000u, 0x00008020u, 0x00000000u, 0x00000000u, 0x00008024u, 0x00000000u,
            0x00000000u, 0x00008028u, 0x00000030u, 0x00000000u, 0x0000802Cu, 0x0007FFFFu, 0x00000000u, 0x00008030u,
            0x01FFFFFFu, 0x00000000u, 0x00008034u, 0x00000031u, 0x00000000u, 0x00008038u, 0x00000000u, 0x00000000u,
            0x0000803Cu, 0x00000000u, 0x00000000u, 0x00008048u, 0x00000000u, 0x00000000u, 0x00008054u, 0x00000000u,
            0x00000000u, 0x00008058u, 0x00000000u, 0x00000000u, 0x0000805Cu, 0x000FC78Fu, 0x00000000u, 0x000080C0u,
            0x2A82301Au, 0x00000000u, 0x000080C4u, 0x05DC01E0u, 0x00000000u, 0x000080C8u, 0x1F402710u, 0x00000000u,
            0x000080CCu, 0x01F40000u, 0x00000000u, 0x000080D0u, 0x00001E1Cu, 0x00000000u, 0x000080D4u, 0x0002AAAAu,
            0x00000000u, 0x000080D8u, 0x02005555u, 0x00000000u, 0x000080DCu, 0x00000000u, 0x00000000u, 0x000080E0u,
            0xFFFFFFFFu, 0x00000000u, 0x000080E4u, 0x0000FFFFu, 0x00000000u, 0x000080E8u, 0x00000000u, 0x00000000u,
            0x000080ECu, 0x00000000u, 0x00000000u, 0x000080F0u, 0x00000000u, 0x00000000u, 0x000080F4u, 0x00000000u,
            0x00000000u, 0x000080F8u, 0x00000000u, 0x00000000u, 0x000080FCu, 0x00000088u, 0x00000000u, 0x00008700u,
            0x00000000u, 0x00000000u, 0x00008704u, 0x0000008Cu, 0x00000000u, 0x00008708u, 0x000000E4u, 0x00000000u,
            0x0000870Cu, 0x000002D5u, 0x00000000u, 0x00008710u, 0x00000000u, 0x00000000u, 0x00008714u, 0x00000000u,
            0x00000000u, 0x00008718u, 0x000000A0u, 0x00000000u, 0x0000871Cu, 0x000001C9u, 0x00000000u, 0x00008720u,
            0x0000002Cu, 0x00000000u, 0x00008724u, 0x0000002Cu, 0x00000000u, 0x00008728u, 0x00000030u, 0x00000000u,
            0x0000872Cu, 0x0000003Cu, 0x00000000u, 0x00008730u, 0x0000002Cu, 0x00000000u, 0x00008734u, 0x0000002Cu,
            0x00000000u, 0x00008738u, 0x00000030u, 0x00000000u, 0x0000873Cu, 0x0000003Cu, 0x00000000u, 0x00008740u,
            0x00000000u, 0x00000000u, 0x00008744u, 0x00000000u, 0x00000000u, 0x00008748u, 0x00000000u, 0x00000000u,
            0x0000874Cu, 0x00000000u, 0x00000000u, 0x00008750u, 0x00000000u, 0x00000000u, 0x00008754u, 0x00000000u,
            0x00000000u, 0x00008758u, 0x00000000u, 0x00000000u, 0x0000875Cu, 0x00000000u, 0x00000000u, 0x00008760u,
            0x000000D5u, 0x00000000u, 0x00008764u, 0x000000DFu, 0x00000000u, 0x00008768u, 0x00000102u, 0x00000000u,
            0x0000876Cu, 0x0000013Au, 0x00000000u, 0x00008770u, 0x00000075u, 0x00000000u, 0x00008774u, 0x0000007Fu,
            0x00000000u, 0x00008778u, 0x000000A2u, 0x00000000u, 0x0000877Cu, 0x00000000u, 0x00000000u, 0x00008100u,
            0x00010002u, 0x00000000u, 0x00008104u, 0x00000001u, 0x00000000u, 0x00008108u, 0x000000C0u, 0x00000000u,
            0x0000810Cu, 0x00000000u, 0x00000000u, 0x00008110u, 0x00000168u, 0x00000000u, 0x00008114u, 0x00000000u,
            0x00000000u, 0x000087C0u, 0x03020100u, 0x00000000u, 0x000087C4u, 0x07060504u, 0x00000000u, 0x000087C8u,
            0x0B0A0908u, 0x00000000u, 0x000087CCu, 0x0F0E0D0Cu, 0x00000000u, 0x000087D0u, 0x13121110u, 0x00000000u,
            0x000087D4u, 0x17161514u, 0x00000000u, 0x000087D8u, 0x1B1A1918u, 0x00000000u, 0x000087DCu, 0x1F1E1D1Cu,
            0x00000000u, 0x000087E0u, 0x03020100u, 0x00000000u, 0x000087E4u, 0x07060504u, 0x00000000u, 0x000087E8u,
            0x0B0A0908u, 0x00000000u, 0x000087ECu, 0x0F0E0D0Cu, 0x00000000u, 0x000087F0u, 0x13121110u, 0x00000000u,
            0x000087F4u, 0x17161514u, 0x00000000u, 0x000087F8u, 0x1B1A1918u, 0x00000000u, 0x000087FCu, 0x1F1E1D1Cu,
            0x00000000u, 0x0000980Cu, 0xAD848E19u, 0x00000000u, 0x00009810u, 0x7D28E000u, 0x00000000u, 0x00009814u,
            0x9C0A9F6Bu, 0x00000000u, 0x0000981Cu, 0x00000000u, 0x00000000u, 0x00009840u, 0x206A017Au, 0x00000000u,
            0x00009854u, 0x00000859u, 0x00000000u, 0x00009900u, 0x00000000u, 0x00000000u, 0x00009904u, 0x00000000u,
            0x00000000u, 0x00009908u, 0x00000000u, 0x00000000u, 0x0000990Cu, 0x00800000u, 0x00000000u, 0x00009910u,
            0x00000001u, 0x00000000u, 0x0000991Cu, 0x00000C80u, 0x00000000u, 0x00009920u, 0x05100000u, 0x00000000u,
            0x00009928u, 0x00000001u, 0x00000000u, 0x0000992Cu, 0x00000004u, 0x00000000u, 0x00009934u, 0x1E1F2022u,
            0x00000000u, 0x00009938u, 0x0A0B0C0Du, 0x00000000u, 0x0000993Cu, 0x0000003Fu, 0x00000000u, 0x00009948u,
            0x9280B212u, 0x00000000u, 0x00009954u, 0x5D50E188u, 0x00000000u, 0x0000995Cu, 0x004B6A8Eu, 0x00000000u,
            0x00009968u, 0x000003CEu, 0x00000000u, 0x00009970u, 0x192FB515u, 0x00000000u, 0x00009978u, 0x00000001u,
            0x00000000u, 0x0000997Cu, 0x00000000u, 0x00000000u, 0x0000A210u, 0x00806333u, 0x00000000u, 0x0000A214u,
            0x00106C10u, 0x00000000u, 0x0000A218u, 0x009C4060u, 0x00000000u, 0x0000A220u, 0x018830C6u, 0x00000000u,
            0x0000A224u, 0x00000400u, 0x00000000u, 0x0000A22Cu, 0x00000000u, 0x00000000u, 0x0000A234u, 0x20202020u,
            0x00000000u, 0x00009938u, 0x20202020u, 0x00000000u, 0x0000A240u, 0x38490A20u, 0x00000000u, 0x0000A244u,
            0x00007BB6u, 0x00000000u, 0x0000A248u, 0x0FFF3FFCu, 0x00000000u
        };

        private static readonly uint[] Rf2425IniModeEnd = new uint[] {
            0x00000030u, 0x00000015u, 0x00000015u, 0x0000801Cu, 0x04E01395u, 0x12E013ABu, 0x00009804u, 0x00000000u,
            0x00000000u, 0x00009828u, 0x05020100u, 0x0A020001u, 0x00009834u, 0x00000E0Eu, 0x00000E0Eu, 0x00009838u,
            0x0000000Bu, 0x0000000Bu, 0x00009844u, 0x13721722u, 0x13721422u, 0x00009848u, 0x00199A65u, 0x00199A65u,
            0x00009850u, 0x0C98B0DAu, 0x0C98B0DAu, 0x00009858u, 0x7EC80D2Eu, 0x7EC80D2Eu, 0x0000985Cu, 0x3139605Eu,
            0x3139605Eu, 0x0000986Cu, 0x050CB081u, 0x050CB081u, 0x00009914u, 0x0000044Cu, 0x00000898u, 0x00009944u,
            0xF7B80D00u, 0xF7B81000u, 0x0000A204u, 0x00000000u, 0x00000000u, 0x0000A208u, 0xD03E6788u, 0xD03E6788u,
            0x0000A20Cu, 0x0052C140u, 0x0052C140u, 0x0000A21Cu, 0x1863800Au, 0x1883800Au, 0x0000A324u, 0xA7CFA7CFu,
            0xA7CFA7CFu, 0x0000A328u, 0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A32Cu, 0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A330u,
            0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A334u, 0xA7CFA7CFu, 0xA7CFA7CFu
        };

        private static readonly uint[] Rf2425IniCommonEnd = new uint[] {
            0x00001230u, 0x000003E0u, 0x00000000u, 0x00008060u, 0x0000000Fu, 0x00000000u, 0x0000809Cu, 0x00000000u,
            0x00000000u, 0x000080A0u, 0x00000000u, 0x00000000u, 0x00008118u, 0x00000000u, 0x00000000u, 0x0000811Cu,
            0x00000000u, 0x00000000u, 0x00008120u, 0x00000000u, 0x00000000u, 0x00008124u, 0x00000000u, 0x00000000u,
            0x00008128u, 0x00000000u, 0x00000000u, 0x0000812Cu, 0x00000000u, 0x00000000u, 0x00008130u, 0x00000000u,
            0x00000000u, 0x00008134u, 0x00000000u, 0x00000000u, 0x00008138u, 0x00000000u, 0x00000000u, 0x0000813Cu,
            0x00000000u, 0x00000000u, 0x00008140u, 0x800003F9u, 0x00000000u, 0x00008144u, 0x00000000u, 0x00000000u,
            0x00009808u, 0x00000000u, 0x00000000u, 0x0000982Cu, 0x0000A000u, 0x00000000u, 0x0000983Cu, 0x00200400u,
            0x00000000u, 0x0000984Cu, 0x1284233Cu, 0x00000000u, 0x00009870u, 0x0000001Fu, 0x00000000u, 0x00009874u,
            0x00000080u, 0x00000000u, 0x00009878u, 0x0000000Eu, 0x00000000u, 0x00009958u, 0x00081FFFu, 0x00000000u,
            0x00009980u, 0x00000000u, 0x00000000u, 0x00009984u, 0x02800000u, 0x00000000u, 0x000099A0u, 0x00000000u,
            0x00000000u, 0x000099DCu, 0xFEBADBE8u, 0x00000000u, 0x000099E0u, 0x00000000u, 0x00000000u, 0x000099E4u,
            0xAAAAAAAAu, 0x00000000u, 0x000099E8u, 0x3C466478u, 0x00000000u, 0x000099ECu, 0x000000AAu, 0x00000000u,
            0x000099F0u, 0x0000000Cu, 0x00000000u, 0x000099F4u, 0x000000FFu, 0x00000000u, 0x000099F8u, 0x00000014u,
            0x00000000u, 0x0000A228u, 0x000009B5u, 0x00000000u, 0x0000A234u, 0x20202020u, 0x00000000u, 0x0000A238u,
            0x20202020u, 0x00000000u, 0x0000A23Cu, 0x93C889AFu, 0x00000000u, 0x0000A24Cu, 0x00000001u, 0x00000000u,
            0x0000A250u, 0x0000A000u, 0x00000000u, 0x0000A254u, 0x00000000u, 0x00000000u, 0x0000A258u, 0x0CC75380u,
            0x00000000u, 0x0000A25Cu, 0x0F0F0F01u, 0x00000000u, 0x0000A260u, 0x5F690F01u, 0x00000000u, 0x0000A264u,
            0x00418A11u, 0x00000000u, 0x0000A268u, 0x00000000u, 0x00000000u, 0x0000A26Cu, 0x0C30C166u, 0x00000000u,
            0x0000A270u, 0x00820820u, 0x00000000u, 0x0000A274u, 0x081A3CAAu, 0x00000000u, 0x0000A278u, 0x1CE739CEu,
            0x00000000u, 0x0000A27Cu, 0x051701CEu, 0x00000000u, 0x0000A300u, 0x16010000u, 0x00000000u, 0x0000A304u,
            0x2C032402u, 0x00000000u, 0x0000A308u, 0x48433E42u, 0x00000000u, 0x0000A30Cu, 0x5A0F500Bu, 0x00000000u,
            0x0000A310u, 0x6C4B624Au, 0x00000000u, 0x0000A314u, 0x7E8B748Au, 0x00000000u, 0x0000A318u, 0x96CF8CCBu,
            0x00000000u, 0x0000A31Cu, 0xA34F9D0Fu, 0x00000000u, 0x0000A320u, 0xA7CFA58Fu, 0x00000000u, 0x0000A348u,
            0x3FFFFFFFu, 0x00000000u, 0x0000A34Cu, 0x3FFFFFFFu, 0x00000000u, 0x0000A350u, 0x3FFFFFFFu, 0x00000000u,
            0x0000A354u, 0x0003FFFFu, 0x00000000u, 0x0000A358u, 0x79A8AA1Fu, 0x00000000u, 0x0000A35Cu, 0x066C420Fu,
            0x00000000u, 0x0000A360u, 0x0F282207u, 0x00000000u, 0x0000A364u, 0x17601685u, 0x00000000u, 0x0000A368u,
            0x1F801104u, 0x00000000u, 0x0000A36Cu, 0x37A00C03u, 0x00000000u, 0x0000A370u, 0x3FC40883u, 0x00000000u,
            0x0000A374u, 0x57C00803u, 0x00000000u, 0x0000A378u, 0x5FD80682u, 0x00000000u, 0x0000A37Cu, 0x7FE00482u,
            0x00000000u, 0x0000A380u, 0x7F3C7BBAu, 0x00000000u, 0x0000A384u, 0xF3307FF0u, 0x00000000u
        };

        private static readonly uint[] Rf5112IniBbGain = new uint[] {
            0x00009B00u, 0x00000000u, 0x00000000u, 0x00009B04u, 0x00000001u, 0x00000000u, 0x00009B08u, 0x00000002u,
            0x00000000u, 0x00009B0Cu, 0x00000003u, 0x00000000u, 0x00009B10u, 0x00000004u, 0x00000000u, 0x00009B14u,
            0x00000005u, 0x00000000u, 0x00009B18u, 0x00000008u, 0x00000000u, 0x00009B1Cu, 0x00000009u, 0x00000000u,
            0x00009B20u, 0x0000000Au, 0x00000000u, 0x00009B24u, 0x0000000Bu, 0x00000000u, 0x00009B28u, 0x0000000Cu,
            0x00000000u, 0x00009B2Cu, 0x0000000Du, 0x00000000u, 0x00009B30u, 0x00000010u, 0x00000000u, 0x00009B34u,
            0x00000011u, 0x00000000u, 0x00009B38u, 0x00000012u, 0x00000000u, 0x00009B3Cu, 0x00000013u, 0x00000000u,
            0x00009B40u, 0x00000014u, 0x00000000u, 0x00009B44u, 0x00000015u, 0x00000000u, 0x00009B48u, 0x00000018u,
            0x00000000u, 0x00009B4Cu, 0x00000019u, 0x00000000u, 0x00009B50u, 0x0000001Au, 0x00000000u, 0x00009B54u,
            0x0000001Bu, 0x00000000u, 0x00009B58u, 0x0000001Cu, 0x00000000u, 0x00009B5Cu, 0x0000001Du, 0x00000000u,
            0x00009B60u, 0x00000020u, 0x00000000u, 0x00009B64u, 0x00000021u, 0x00000000u, 0x00009B68u, 0x00000022u,
            0x00000000u, 0x00009B6Cu, 0x00000023u, 0x00000000u, 0x00009B70u, 0x00000024u, 0x00000000u, 0x00009B74u,
            0x00000025u, 0x00000000u, 0x00009B78u, 0x00000028u, 0x00000000u, 0x00009B7Cu, 0x00000029u, 0x00000000u,
            0x00009B80u, 0x0000002Au, 0x00000000u, 0x00009B84u, 0x0000002Bu, 0x00000000u, 0x00009B88u, 0x0000002Cu,
            0x00000000u, 0x00009B8Cu, 0x0000002Du, 0x00000000u, 0x00009B90u, 0x00000030u, 0x00000000u, 0x00009B94u,
            0x00000031u, 0x00000000u, 0x00009B98u, 0x00000032u, 0x00000000u, 0x00009B9Cu, 0x00000033u, 0x00000000u,
            0x00009BA0u, 0x00000034u, 0x00000000u, 0x00009BA4u, 0x00000035u, 0x00000000u, 0x00009BA8u, 0x00000035u,
            0x00000000u, 0x00009BACu, 0x00000035u, 0x00000000u, 0x00009BB0u, 0x00000035u, 0x00000000u, 0x00009BB4u,
            0x00000035u, 0x00000000u, 0x00009BB8u, 0x00000035u, 0x00000000u, 0x00009BBCu, 0x00000035u, 0x00000000u,
            0x00009BC0u, 0x00000035u, 0x00000000u, 0x00009BC4u, 0x00000035u, 0x00000000u, 0x00009BC8u, 0x00000035u,
            0x00000000u, 0x00009BCCu, 0x00000035u, 0x00000000u, 0x00009BD0u, 0x00000035u, 0x00000000u, 0x00009BD4u,
            0x00000035u, 0x00000000u, 0x00009BD8u, 0x00000035u, 0x00000000u, 0x00009BDCu, 0x00000035u, 0x00000000u,
            0x00009BE0u, 0x00000035u, 0x00000000u, 0x00009BE4u, 0x00000035u, 0x00000000u, 0x00009BE8u, 0x00000035u,
            0x00000000u, 0x00009BECu, 0x00000035u, 0x00000000u, 0x00009BF0u, 0x00000035u, 0x00000000u, 0x00009BF4u,
            0x00000035u, 0x00000000u, 0x00009BF8u, 0x00000010u, 0x00000000u, 0x00009BFCu, 0x0000001Au, 0x00000000u
        };

        private static readonly uint[] RfGain2425 = new uint[] {
            0x00009A00u, 0x00000000u, 0x00009A04u, 0x00000040u, 0x00009A08u, 0x00000080u, 0x00009A0Cu, 0x00000181u,
            0x00009A10u, 0x000001C1u, 0x00009A14u, 0x00000001u, 0x00009A18u, 0x00000041u, 0x00009A1Cu, 0x00000081u,
            0x00009A20u, 0x00000188u, 0x00009A24u, 0x000001C8u, 0x00009A28u, 0x00000008u, 0x00009A2Cu, 0x00000048u,
            0x00009A30u, 0x00000088u, 0x00009A34u, 0x00000189u, 0x00009A38u, 0x000001C9u, 0x00009A3Cu, 0x00000009u,
            0x00009A40u, 0x00000049u, 0x00009A44u, 0x00000089u, 0x00009A48u, 0x000001B0u, 0x00009A4Cu, 0x000001F0u,
            0x00009A50u, 0x00000030u, 0x00009A54u, 0x00000070u, 0x00009A58u, 0x00000171u, 0x00009A5Cu, 0x000001B1u,
            0x00009A60u, 0x000001F1u, 0x00009A64u, 0x00000031u, 0x00009A68u, 0x00000071u, 0x00009A6Cu, 0x000001B8u,
            0x00009A70u, 0x000001F8u, 0x00009A74u, 0x00000038u, 0x00009A78u, 0x00000078u, 0x00009A7Cu, 0x000000B8u,
            0x00009A80u, 0x000001B9u, 0x00009A84u, 0x000001F9u, 0x00009A88u, 0x00000039u, 0x00009A8Cu, 0x00000079u,
            0x00009A90u, 0x000000B9u, 0x00009A94u, 0x000000F9u, 0x00009A98u, 0x000000F9u, 0x00009A9Cu, 0x000000F9u,
            0x00009AA0u, 0x000000F9u, 0x00009AA4u, 0x000000F9u, 0x00009AA8u, 0x000000F9u, 0x00009AACu, 0x000000F9u,
            0x00009AB0u, 0x000000F9u, 0x00009AB4u, 0x000000F9u, 0x00009AB8u, 0x000000F9u, 0x00009ABCu, 0x000000F9u,
            0x00009AC0u, 0x000000F9u, 0x00009AC4u, 0x000000F9u, 0x00009AC8u, 0x000000F9u, 0x00009ACCu, 0x000000F9u,
            0x00009AD0u, 0x000000F9u, 0x00009AD4u, 0x000000F9u, 0x00009AD8u, 0x000000F9u, 0x00009ADCu, 0x000000F9u,
            0x00009AE0u, 0x000000F9u, 0x00009AE4u, 0x000000F9u, 0x00009AE8u, 0x000000F9u, 0x00009AECu, 0x000000F9u,
            0x00009AF0u, 0x000000F9u, 0x00009AF4u, 0x000000F9u, 0x00009AF8u, 0x000000F9u, 0x00009AFCu, 0x000000F9u
        };

        private static readonly uint[] Rfb2425 = new uint[] {
            0x00000001u, 0x000098D4u, 0x00000020u, 0x00000020u, 0x00000002u, 0x000098D0u, 0x02001408u, 0x02001408u,
            0x00000003u, 0x000098DCu, 0x00E020C0u, 0x00E020C0u, 0x00000006u, 0x0000989Cu, 0x10000000u, 0x10000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x002A0000u, 0x002A0000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00100000u, 0x00100000u,
            0x00000006u, 0x0000989Cu, 0x00020000u, 0x00020000u, 0x00000006u, 0x0000989Cu, 0x00730000u, 0x00730000u,
            0x00000006u, 0x0000989Cu, 0x00F80000u, 0x00F80000u, 0x00000006u, 0x0000989Cu, 0x00E70000u, 0x00E70000u,
            0x00000006u, 0x0000989Cu, 0x00140000u, 0x00140000u, 0x00000006u, 0x0000989Cu, 0x00910040u, 0x00910040u,
            0x00000006u, 0x0000989Cu, 0x0007001Au, 0x0007001Au, 0x00000006u, 0x0000989Cu, 0x00410000u, 0x00410000u,
            0x00000006u, 0x0000989Cu, 0x00810060u, 0x00810060u, 0x00000006u, 0x0000989Cu, 0x00020803u, 0x00020803u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00001660u, 0x00001660u, 0x00000006u, 0x0000989Cu, 0x00001688u, 0x00001688u,
            0x00000006u, 0x000098C4u, 0x00000001u, 0x00000001u, 0x00000007u, 0x0000989Cu, 0x00006400u, 0x00006400u,
            0x00000007u, 0x0000989Cu, 0x00000800u, 0x00000800u, 0x00000007u, 0x000098CCu, 0x0000000Eu, 0x0000000Eu
        };
        #endregion
    }
}
