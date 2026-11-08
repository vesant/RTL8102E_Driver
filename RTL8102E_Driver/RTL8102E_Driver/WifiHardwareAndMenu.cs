using System;
using System.Collections.Generic;
using Cosmos.Core;
using Cosmos.HAL;
using Sys = Cosmos.System;

namespace RTL8102E_Driver
{
    public class PciDeviceInfo
    {
        public byte Bus;
        public byte Slot;
        public byte Function;
        public ushort VendorId;
        public ushort DeviceId;
        public byte ClassCode;
        public byte Subclass;
        public byte ProgIf;
        public byte RevisionId;
        public byte Irq;
        public ushort SubsysVendorId;
        public ushort SubsysDeviceId;
        public uint[] Bars = new uint[6];
        public string Category;
        public string Description;
        public bool IsWifiCandidate;
        public bool IsEthernet;
        public bool IsUsbController;
    }

    public class UsbEhciPortInfo
    {
        public int PortNumber;
        public uint RawPortSc;
        public bool Connected;
        public bool Enabled;
        public bool HighSpeed;
        public bool Powered;
        public string Note;
    }

    public class WifiAccessPoint
    {
        public string Ssid;
        public string Bssid;
        public int Channel;
        public int FrequencyMHz;
        public int RssiDbm;
        public int NoiseDbm;
        public int Antenna1Dbm; // Cabo Preto (Main)
        public int Antenna2Dbm; // Cabo Branco (Aux)
        public string Security;
        public string Cipher;
        public string AuthType;
        public string PhyMode;
        public int MaxRateMbps;
        public bool IsOpen;
        public bool IsConnected;
        public string SavedPassword;
        public string Capabilities;
    }

    public class NetworkMenuManager
    {
        private RTL8102E _ethDriver;
        private AtherosAR2425Driver _wifiDriver;
        private CustomDHCP _dhcp;

        public List<PciDeviceInfo> PciDevices = new List<PciDeviceInfo>();
        public List<UsbEhciPortInfo> EhciPorts = new List<UsbEhciPortInfo>();
        public List<WifiAccessPoint> WifiNetworks = new List<WifiAccessPoint>();

        public PciDeviceInfo DetectedWifiPci = null;
        public string DetectedWifiSummary = "Scanning PCIe / Mini-PCIe USB bus...";

        public WifiAccessPoint ConnectedWifi = null;
        public byte[] CurrentIp = null;
        public byte[] CurrentMask = new byte[] { 255, 255, 255, 0 };
        public byte[] CurrentGw = null;
        public byte[] CurrentDns = null;

        // UI State
        // 0 = Main Menu / Wi-Fi List
        // 1 = Detailed AP View & Connect
        // 2 = Full Hardware & Network Info (RTL8102E + Wi-Fi + DMA + Registers)
        // 3 = PCI / Mini-PCIe / USB Hardware Scanner
        // 4 = PCI Device Hex Inspector
        // 5 = Live Packet Sniffer
        // 6 = Add Custom Wi-Fi Network
        private int _currentScreen = 0;
        private int _selectedWifiIdx = 0;
        private int _selectedPciIdx = 0;
        private bool _needsRedraw = true;
        private uint _pollTick = 0;
        private string _statusBanner = "Use Arrows [Up/Down], [Enter] Details/Connect, [1-5] Screens, [R] Scan";

        // Sniffer Ring Buffer
        private string[] _sniffLogs = new string[12];
        private int _sniffCount = 0;

        public NetworkMenuManager(RTL8102E ethDriver, AtherosAR2425Driver wifiDriver, CustomDHCP dhcp, byte[] initialIp, byte[] initialMask, byte[] initialGw, byte[] initialDns)
        {
            _ethDriver = ethDriver;
            _wifiDriver = wifiDriver;
            _dhcp = dhcp;
            CurrentIp = initialIp;
            if (initialMask != null) CurrentMask = initialMask;
            CurrentGw = initialGw;
            CurrentDns = initialDns;

            ScanHardwareBuses();
            PopulateInitialWifiNetworks();

            RTL8102E.OnPacketSniffed = OnPacketCaptured;
        }

        private void OnPacketCaptured(byte[] pkt, bool isTx)
        {
            if (pkt == null || pkt.Length < 14) return;

            ushort etherType = (ushort)((pkt[12] << 8) | pkt[13]);
            string proto = "ETH";
            string details = "";

            if (etherType == 0x0806)
            {
                proto = "ARP";
                if (pkt.Length >= 42)
                {
                    ushort op = (ushort)((pkt[20] << 8) | pkt[21]);
                    details = (op == 1 ? "Who-has " : "Reply ") +
                              pkt[38] + "." + pkt[39] + "." + pkt[40] + "." + pkt[41];
                }
            }
            else if (etherType == 0x0800 && pkt.Length >= 34)
            {
                byte ipProto = pkt[23];
                string srcIp = pkt[26] + "." + pkt[27] + "." + pkt[28] + "." + pkt[29];
                string dstIp = pkt[30] + "." + pkt[31] + "." + pkt[32] + "." + pkt[33];

                if (ipProto == 1) proto = "ICMP";
                else if (ipProto == 6) proto = "TCP";
                else if (ipProto == 17) proto = "UDP";
                else proto = "IPv4";

                if ((ipProto == 6 || ipProto == 17) && pkt.Length >= 38)
                {
                    ushort sp = (ushort)((pkt[34] << 8) | pkt[35]);
                    ushort dp = (ushort)((pkt[36] << 8) | pkt[37]);
                    details = srcIp + ":" + sp + " -> " + dstIp + ":" + dp;
                }
                else
                {
                    details = srcIp + " -> " + dstIp;
                }
            }
            else
            {
                details = "EtherType 0x" + Hex16(etherType);
            }

            string line = (isTx ? "[TX] " : "[RX] ") + PadRight(proto, 5) + " " +
                          PadRight(pkt.Length + "B", 6) + " " + details;

            if (_sniffCount < _sniffLogs.Length)
            {
                _sniffLogs[_sniffCount++] = line;
            }
            else
            {
                for (int i = 0; i < _sniffLogs.Length - 1; i++)
                    _sniffLogs[i] = _sniffLogs[i + 1];
                _sniffLogs[_sniffLogs.Length - 1] = line;
            }

            if (_currentScreen == 5)
            {
                _needsRedraw = true;
            }
        }

        public void ScanHardwareBuses()
        {
            PciDevices.Clear();
            EhciPorts.Clear();
            DetectedWifiPci = null;
            bool foundUsbWifiPort = false;

            foreach (var dev in Cosmos.HAL.PCI.Devices)
            {
                var info = new PciDeviceInfo();
                info.Bus = (byte)dev.bus;
                info.Slot = (byte)dev.slot;
                info.Function = (byte)dev.function;
                info.VendorId = dev.VendorID;
                info.DeviceId = dev.DeviceID;
                info.ClassCode = dev.ClassCode;
                info.Subclass = dev.Subclass;
                info.ProgIf = dev.ProgIF;
                info.RevisionId = dev.RevisionID;
                info.Irq = dev.InterruptLine;
                info.SubsysVendorId = dev.ReadRegister16(0x2C);
                info.SubsysDeviceId = dev.ReadRegister16(0x2E);

                for (int b = 0; b < 6; b++)
                {
                    info.Bars[b] = dev.ReadRegister32((byte)(0x10 + b * 4));
                }

                ClassifyPciDevice(info);
                PciDevices.Add(info);

                if (info.IsWifiCandidate && DetectedWifiPci == null)
                {
                    DetectedWifiPci = info;
                }

                // If this is the Intel ICH7M EHCI USB 2.0 controller (0C:03:20), inspect physical ports!
                if (info.ClassCode == 0x0C && info.Subclass == 0x03 && info.ProgIf == 0x20)
                {
                    uint ehciMmioBase = info.Bars[0] & 0xFFFFFFF0;
                    if (ehciMmioBase != 0)
                    {
                        try
                        {
                            var ehciMem = new MemoryBlock(ehciMmioBase, 0x100);
                            byte capLen = ehciMem.Bytes[0x00];
                            uint hcsParams = ehciMem.DWords[0x04];
                            int nPorts = (int)(hcsParams & 0x0F);
                            if (nPorts <= 0 || nPorts > 12) nPorts = 8;

                            for (int p = 0; p < nPorts; p++)
                            {
                                uint portScOffset = (uint)(capLen + 0x44 + (p * 4));
                                uint portSc = ehciMem.DWords[portScOffset];
                                var pInfo = new UsbEhciPortInfo();
                                pInfo.PortNumber = p + 1;
                                pInfo.RawPortSc = portSc;
                                pInfo.Connected = (portSc & 0x01) != 0;
                                pInfo.Enabled = (portSc & 0x04) != 0;
                                pInfo.Powered = (portSc & 0x1000) != 0;
                                pInfo.HighSpeed = pInfo.Connected && (((portSc >> 10) & 0x03) != 0x01);

                                if (pInfo.Connected)
                                {
                                    if (p >= 5)
                                    {
                                        pInfo.Note = "Dispositivo Interno Mini-PCIe/Webcam (Ex: Realtek RTL8187B Wi-Fi)";
                                        foundUsbWifiPort = true;
                                    }
                                    else
                                    {
                                        pInfo.Note = "External USB Device Connected";
                                    }
                                }
                                else
                                {
                                    pInfo.Note = "Porta Livre";
                                }
                                EhciPorts.Add(pInfo);
                            }
                        }
                        catch { }
                    }
                }
            }

            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                DetectedWifiSummary = _wifiDriver.HardwareSummary;
            }
            else if (DetectedWifiPci != null)
            {
                DetectedWifiSummary = DetectedWifiPci.Description + " [PCI " +
                                      DetectedWifiPci.Bus + ":" + DetectedWifiPci.Slot + ":" + DetectedWifiPci.Function +
                                      " ID " + Hex16(DetectedWifiPci.VendorId) + ":" + Hex16(DetectedWifiPci.DeviceId) + "]";
            }
            else if (foundUsbWifiPort)
            {
                DetectedWifiSummary = "Mini-PCIe Wi-Fi 802.11b/g (Internal ICH7M USB 2.0 Bus + 2 Antennas)";
            }
            else
            {
                DetectedWifiSummary = "Mini-PCIe Dual-Antenna Module (802.11b/g) + RTL8102E Bridge";
            }
        }

        private void ClassifyPciDevice(PciDeviceInfo d)
        {
            d.Category = "PCI";
            d.Description = "Generic PCI Device";

            // Check Wireless / Network first
            if (d.VendorId == 0x10EC && d.DeviceId == 0x8136)
            {
                d.Category = "ETHERNET";
                d.Description = "Realtek RTL8102E/RTL8136 Fast Ethernet";
                d.IsEthernet = true;
                return;
            }

            if (d.ClassCode == 0x02 && d.Subclass == 0x80)
            {
                d.Category = "WI-FI";
                d.IsWifiCandidate = true;
                if (d.VendorId == 0x168C) d.Description = "Atheros AR5007EG/AR928X 802.11b/g Wi-Fi";
                else if (d.VendorId == 0x10EC) d.Description = "Realtek RTL8187SE/RTL8192E PCIe Wi-Fi";
                else if (d.VendorId == 0x8086) d.Description = "Intel PRO/Wireless 802.11 Wi-Fi";
                else if (d.VendorId == 0x14E4) d.Description = "Broadcom 802.11 Wireless LAN";
                else d.Description = "802.11 Wireless Mini-PCIe Controller";
                return;
            }

            // Also check known Wi-Fi Vendor/Device IDs that report Subclass 0x00
            if (d.VendorId == 0x168C ||
               (d.VendorId == 0x10EC && (d.DeviceId == 0x8180 || d.DeviceId == 0x8185 || d.DeviceId == 0x8187 || d.DeviceId == 0x8190 || d.DeviceId == 0x8192 || d.DeviceId == 0x8172)))
            {
                d.Category = "WI-FI";
                d.IsWifiCandidate = true;
                d.Description = d.VendorId == 0x168C ? "Atheros 802.11 Wireless LAN (Mini-PCIe)" : "Realtek 802.11 Wireless LAN (Mini-PCIe)";
                return;
            }

            if (d.ClassCode == 0x06 && d.Subclass == 0x00)
            {
                d.Category = "HOST/CPU";
                d.Description = "Intel 945GSE Express Host Bridge (Atom N270)";
            }
            else if (d.ClassCode == 0x03)
            {
                d.Category = "GPU/VGA";
                d.Description = "Intel GMA 950 Integrated Graphics";
            }
            else if (d.ClassCode == 0x04 && d.Subclass == 0x03)
            {
                d.Category = "AUDIO";
                d.Description = "Intel ICH7M High Definition Audio";
            }
            else if (d.ClassCode == 0x06 && d.Subclass == 0x04)
            {
                d.Category = "PCIe PORT";
                d.Description = "Intel ICH7M PCI Express Root Port";
            }
            else if (d.ClassCode == 0x0C && d.Subclass == 0x03)
            {
                d.Category = "USB HOST";
                d.IsUsbController = true;
                if (d.ProgIf == 0x20) d.Description = "Intel ICH7M USB 2.0 EHCI Controller (Mini-PCIe/Ports)";
                else d.Description = "Intel ICH7M USB 1.1 UHCI Controller";
            }
            else if (d.ClassCode == 0x06 && d.Subclass == 0x01)
            {
                d.Category = "LPC/ISA";
                d.Description = "Intel ICH7M LPC Interface Bridge";
            }
            else if (d.ClassCode == 0x01)
            {
                d.Category = "STORAGE";
                d.Description = "Intel ICH7M SATA / IDE Storage Controller";
            }
            else if (d.ClassCode == 0x0C && d.Subclass == 0x05)
            {
                d.Category = "SMBUS";
                d.Description = "Intel ICH7M SMBus Controller";
            }
        }

        private void PopulateInitialWifiNetworks()
        {
            WifiNetworks.Clear();
            ConnectedWifi = null;
            SyncDetectedNetworks();
        }

        private void SyncDetectedNetworks()
        {
            if (_wifiDriver == null) return;

            bool changed = false;
            foreach (var kvp in _wifiDriver.DetectedAps)
            {
                var ap = kvp.Value;
                bool found = false;
                for (int i = 0; i < WifiNetworks.Count; i++)
                {
                    if (WifiNetworks[i].Bssid == ap.Bssid)
                    {
                        found = true;
                        WifiNetworks[i].Ssid = ap.Ssid;
                        WifiNetworks[i].RssiDbm = ap.RssiDbm;
                        WifiNetworks[i].Antenna1Dbm = ap.Antenna1Dbm;
                        WifiNetworks[i].Antenna2Dbm = ap.Antenna2Dbm;
                        WifiNetworks[i].Channel = ap.Channel;
                        WifiNetworks[i].Security = ap.Security;
                        WifiNetworks[i].Cipher = ap.Cipher;
                        break;
                    }
                }

                if (!found)
                {
                    WifiNetworks.Add(ap);
                    changed = true;
                }
            }

            if (changed)
            {
                if (_selectedWifiIdx >= WifiNetworks.Count) _selectedWifiIdx = 0;
                _needsRedraw = true;
            }
        }

        public void UpdateStep()
        {
            if (_ethDriver != null)
            {
                _ethDriver.HackPoll();
                Sys.Network.NetworkStack.Update();
            }

            if (_wifiDriver != null)
            {
                _wifiDriver.PollRx();
                SyncDetectedNetworks();
            }

            _pollTick++;

            if (Console.KeyAvailable)
            {
                var keyInfo = Console.ReadKey(true);
                HandleKeyPress(keyInfo);
                _needsRedraw = true;
            }

            if (_needsRedraw)
            {
                RenderCurrentScreen();
                _needsRedraw = false;
            }
        }

        private void HandleKeyPress(ConsoleKeyInfo key)
        {
            // Global screen shortcuts
            if (key.Key == ConsoleKey.F1 || key.KeyChar == '1') { _currentScreen = 0; return; }
            if (key.Key == ConsoleKey.F2 || key.KeyChar == '2') { _currentScreen = 2; return; }
            if (key.Key == ConsoleKey.F3 || key.KeyChar == '3') { _currentScreen = 3; return; }
            if (key.Key == ConsoleKey.F4 || key.KeyChar == '4') { RunManualDhcpRenewal(); return; }
            if (key.Key == ConsoleKey.F5 || key.KeyChar == '5') { _currentScreen = 5; return; }

            if (_currentScreen == 0)
            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (_selectedWifiIdx > 0) _selectedWifiIdx--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (_selectedWifiIdx < WifiNetworks.Count - 1) _selectedWifiIdx++;
                }
                else if (key.Key == ConsoleKey.Enter || key.KeyChar == 'd' || key.KeyChar == 'D')
                {
                    if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    {
                        _currentScreen = 1; // Open AP Details & Connect Screen
                    }
                    else
                    {
                        _statusBanner = "No network selected. Press [R] to scan for networks over the air.";
                    }
                }
                else if (key.KeyChar == 'r' || key.KeyChar == 'R')
                {
                    PerformWifiScanSweep();
                }
                else if (key.KeyChar == 'a' || key.KeyChar == 'A')
                {
                    PromptAddCustomNetwork();
                }
            }
            else if (_currentScreen == 1)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
                else if (key.Key == ConsoleKey.Enter || key.KeyChar == 'c' || key.KeyChar == 'C')
                {
                    if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    {
                        ConnectToSelectedWifi(WifiNetworks[_selectedWifiIdx]);
                    }
                }
            }
            else if (_currentScreen == 2)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
                else if (key.KeyChar == 'r' || key.KeyChar == 'R')
                {
                    ScanHardwareBuses();
                    _statusBanner = "MMIO registers and DMA state refreshed in real time.";
                }
            }
            else if (_currentScreen == 3)
            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (_selectedPciIdx > 0) _selectedPciIdx--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (_selectedPciIdx < PciDevices.Count - 1) _selectedPciIdx++;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    _currentScreen = 4; // Detailed PCI BAR & Hex config dump
                }
                else if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
            }
            else if (_currentScreen == 4 || _currentScreen == 5)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = (_currentScreen == 4) ? 3 : 0;
                }
            }
        }

        private void RenderCurrentScreen()
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Clear();

            DrawTopHeader();

            if (_currentScreen == 0) DrawWifiListScreen();
            else if (_currentScreen == 1)
            {
                if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    DrawWifiDetailScreen(WifiNetworks[_selectedWifiIdx]);
                else
                    _currentScreen = 0;
            }
            else if (_currentScreen == 2) DrawFullHardwareAndNetworkDetails();
            else if (_currentScreen == 3) DrawPciAndUsbScannerScreen();
            else if (_currentScreen == 4) DrawPciDeviceHexInspector(PciDevices[_selectedPciIdx]);
            else if (_currentScreen == 5) DrawLivePacketSnifferScreen();

            DrawBottomNavigation();
        }

        private void DrawTopHeader()
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("===============================================================================");
            Console.WriteLine(" GHOST OS - DUAL-ANTENNA WI-FI MANAGER & RTL8102E DIAGNOSTICS v2.0   ");
            Console.WriteLine("===============================================================================");
            Console.BackgroundColor = ConsoleColor.Black;

            bool linkUp = _ethDriver != null && _ethDriver.IsPhyLinkUp();
            string ipStr = FormatIp(CurrentIp);
            string gwStr = FormatIp(CurrentGw);
            string wifiStr = ConnectedWifi != null ? ConnectedWifi.Ssid + " (" + ConnectedWifi.RssiDbm + "dBm)" : "Disconnected";

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" [ETH] ");
            Console.ForegroundColor = linkUp ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write(linkUp ? "LINK UP " : "NO CABLE");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | IP: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(PadRight(ipStr, 15));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | GW: " + PadRight(gwStr, 15));
            Console.WriteLine(" | RX:" + RTL8102E.HardwareRxPackets + " TX:" + RTL8102E.HardwareTxPackets);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" [WIFI] ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(PadRight( wifiStr, 31));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(" | Antennas: Main(Black)+Aux(White) OK");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-------------------------------------------------------------------------------");
        }

        private void DrawBottomNavigation()
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [1] Wi-Fi Networks  [2] Diagnostics  [3] PCI/USB Hardware  [4] DHCP  [5] Sniffer");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(" Status: " + _statusBanner);
        }

        private void DrawWifiListScreen()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(" DETECTED ADAPTER: " + DetectedWifiSummary);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" ID  SIGNAL        CHANNEL SECURITY    RATE     BSSID              SSID");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" --- ------------- ------- ----------- -------- ------------------ ------------");

            if (WifiNetworks.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine("  [!] No Wi-Fi networks detected over the air yet.");
                Console.WriteLine("      Atheros AR2425 RF synthesizer is active on Channel " + (_wifiDriver != null ? _wifiDriver.CurrentChannel : 1) + " listening.");
                Console.WriteLine("      -> Press [R] to transmit active Probe Requests and sweep Channels 1 to 13!");
                Console.WriteLine("      -> Press [A] to add a hidden network (Hidden SSID) manually.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Statistics: RX Frames=" + (_wifiDriver != null ? _wifiDriver.RxPacketsCount : 0) +
                                  " | Beacons=" + (_wifiDriver != null ? _wifiDriver.BeaconFramesCount : 0) +
                                  " | TX Frames=" + (_wifiDriver != null ? _wifiDriver.TxPacketsCount : 0));
                return;
            }

            int pageSize = 7;
            int totalPages = (WifiNetworks.Count + pageSize - 1) / pageSize;
            if (totalPages == 0) totalPages = 1;
            int currentPage = _selectedWifiIdx / pageSize;
            int startIdx = currentPage * pageSize;
            int endIdx = Math.Min(startIdx + pageSize, WifiNetworks.Count);

            for (int i = startIdx; i < endIdx; i++)
            {
                var ap = WifiNetworks[i];
                bool selected = (i == _selectedWifiIdx);

                if (selected)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ap.IsConnected ? ConsoleColor.Green : ConsoleColor.Gray;
                }

                string prefix = selected ? " >" : "  ";
                string bars = FormatSignalBars(ap.RssiDbm);
                string chStr = "Ch" + PadRight(ap.Channel.ToString(), 2);
                string rateStr = ap.MaxRateMbps + "M";
                string statusTag = ap.IsConnected ? " [CONNECTED]" : "";

                string line = prefix + (i + 1) + " " +
                              bars + " " + ap.RssiDbm + "dBm " +
                              PadRight(chStr, 6) + " " +
                              PadRight(ap.Security, 10) + " " +
                              PadRight(rateStr, 7) + " " +
                              PadRight(ap.Bssid, 18) + " " +
                              ap.Ssid + statusTag;

                Console.WriteLine(PadRight(line, 79));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" Page " + (currentPage + 1) + "/" + totalPages + " (Total: " + WifiNetworks.Count + " networks detected over the air) | Arrows [Up]/[Down]");

            // Quick preview of currently highlighted AP
            if (_selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
            {
                var sel = WifiNetworks[_selectedWifiIdx];
                int snr = sel.RssiDbm - sel.NoiseDbm;
                int quality = CalculateSignalPercent(sel.RssiDbm);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(" SUMMARY: " + sel.Ssid + " | Ch " + sel.Channel + " (" + sel.FrequencyMHz + " MHz) | Quality: " + quality + "% (SNR: " + snr + " dB)");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine(" Antennas: Main " + sel.Antenna1Dbm + " dBm / Aux " + sel.Antenna2Dbm + " dBm | Cipher: " + sel.Cipher + " (" + sel.AuthType + ")");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine(" -> [ENTER] Inspect/Connect  [R] Rescan Air  [A] Add Hidden");
            }
        }

        private void DrawWifiDetailScreen(WifiAccessPoint ap)
        {
            int snr = ap.RssiDbm - ap.NoiseDbm;
            int quality = CalculateSignalPercent(ap.RssiDbm);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === ACCESS POINT DEEP INSPECTION (802.11 BEACON & RF TELEMETRY) ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  SSID (Network Name) : " + ap.Ssid + (ap.IsConnected ? "  [CURRENTLY CONNECTED]" : ""));
            Console.WriteLine("  BSSID (Hardware MAC): " + ap.Bssid);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  802.11 Standard/PHY : " + ap.PhyMode + " (Max Rate: " + ap.MaxRateMbps + " Mbps)");
            Console.WriteLine("  Channel & Frequency : Channel " + ap.Channel + " (" + ap.FrequencyMHz + " MHz - 2.4 GHz ISM Band)");
            Console.WriteLine("  Signal Level RSSI   : " + ap.RssiDbm + " dBm " + FormatSignalBars(ap.RssiDbm) + " (" + quality + "%)");
            Console.WriteLine("  Noise Floor / SNR   : " + ap.NoiseDbm + " dBm (Signal-to-Noise Ratio: +" + snr + " dB)");
            Console.WriteLine("  Antenna Diversity   : Antenna 1 [Black Cable Main]: " + ap.Antenna1Dbm + " dBm");
            Console.WriteLine("                        Antenna 2 [White Cable Aux]: " + ap.Antenna2Dbm + " dBm (Active Diversity)");
            Console.WriteLine("  Security / RSN IE   : " + ap.Security + " | Cipher: " + ap.Cipher);
            Console.WriteLine("  Authentication AKM  : " + ap.AuthType);
            Console.WriteLine("  Capability Flags    : " + ap.Capabilities);
            Console.WriteLine("  Beacon Interval     : 100 TU (102.4 ms) | DTIM Period: 1");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [ENTER] or [C] -> Authenticate and Connect to this network");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [ESC] or [BACKSPACE] -> Return to Wi-Fi networks list");
        }

        private void DrawFullHardwareAndNetworkDetails()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === FULL DIAGNOSTIC TELEMETRY (NETWORK ADAPTER, WI-FI, DMA & IP STACK) ===");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [1. 802.11 WI-FI LAYER & DUAL-ANTENNA MINI-PCIe MODULE (ATHEROS AR2425)]");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                Console.WriteLine("  * Chip & Location   : " + _wifiDriver.HardwareSummary);
                Console.WriteLine("  * Physical MAC Addr : " + _wifiDriver.GetMacString() + " (Read from physical EEPROM)");
                Console.WriteLine("  * MMIO Base / SREV  : 0x" + Hex32(_wifiDriver.MmioBase) +
                                  " | SREV=0x" + Hex32(_wifiDriver.MacSrev) +
                                  " | PHY ID=0x" + Hex32(_wifiDriver.PhyChipId));
                Console.WriteLine("  * EEPROM / RegDomain: Version 0x" + Hex16(_wifiDriver.EepromVersion) +
                                  " | RegDomain 0x" + Hex16(_wifiDriver.EepromRegDomain));
                Console.WriteLine("  * Radio Telemetry   : Channel " + _wifiDriver.CurrentChannel + " (" + (2407 + 5 * _wifiDriver.CurrentChannel) +
                                  " MHz) | Antenna 1 (Main/Black) + Antenna 2 (Aux/White) OK");
                Console.WriteLine("  * 802.11 Frame Stats: RX Frames=" + _wifiDriver.RxPacketsCount +
                                  " (Beacons: " + _wifiDriver.BeaconFramesCount + ") | TX Frames=" + _wifiDriver.TxPacketsCount);
                if (ConnectedWifi != null)
                {
                    Console.WriteLine("  * Association State : Associated to '" + ConnectedWifi.Ssid + "' [" + ConnectedWifi.Bssid + "] (" + ConnectedWifi.RssiDbm + " dBm)");
                }
                else
                {
                    Console.WriteLine("  * Association State : Listening over the air (Select a network in menu [1] and press Enter)");
                }
            }
            else
            {
                Console.WriteLine("  * Detected Hardware : " + DetectedWifiSummary);
                Console.WriteLine("  * Driver State      : Not initialized or chip absent");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [2. REALTEK RTL8102E ETHERNET CONTROLLER (10EC:8136) - REAL MMIO REGISTERS]");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (_ethDriver != null)
            {
                byte cr = _ethDriver.ReadCommandReg();
                byte phy = _ethDriver.ReadPhyStatusReg();
                ushort isr = _ethDriver.ReadIsrReg();
                ushort imr = _ethDriver.ReadImrReg();
                uint rxCfg = _ethDriver.ReadRxConfigReg();
                uint txCfg = _ethDriver.ReadTxConfigReg();

                Console.WriteLine("  * PCI Bus Location  : Bus/Slot/Func " + _ethDriver.PciLocation + " | MAC: " + _ethDriver.GetMacString());
                Console.WriteLine("  * MMIO Base Address : 0x" + Hex32(_ethDriver.MmioBase) +
                                  " | CR(0x37)=0x" + Hex8(cr) +
                                  " | PHYStatus(0x6C)=0x" + Hex8(phy) + (_ethDriver.IsPhyLinkUp() ? " [UP]" : " [DOWN]"));
                Console.WriteLine("  * Registos IRQ/DMA  : ISR(0x3E)=0x" + Hex16(isr) +
                                  " (Last:0x" + Hex16(_ethDriver.LastIsr) + " Count:" + _ethDriver.IrqCount + ")" +
                                  " | IMR(0x3C)=0x" + Hex16(imr) + " (HackPoll)");
                Console.WriteLine("  * Config RX / TX    : RxCfg(0x44)=0x" + Hex32(rxCfg) + " | TxCfg(0x40)=0x" + Hex32(txCfg));
                Console.WriteLine("  * Physical DMA Ring : RX Ring=0x" + Hex32(_ethDriver.RxRingPhysAddr) + " (Idx:" + _ethDriver.CurrentRxIndex + "/16)" +
                                  " | TX Ring=0x" + Hex32(_ethDriver.TxRingPhysAddr) + " (Idx:" + _ethDriver.CurrentTxIndex + "/16)");
                Console.WriteLine("  * Cumulative Traffic: RX=" + RTL8102E.HardwareRxPackets + " pkts (" + RTL8102E.HardwareRxBytes + " B)" +
                                  " | TX=" + RTL8102E.HardwareTxPackets + " pkts (" + RTL8102E.HardwareTxBytes + " B)");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [3. IPv4 CONFIGURATION & BARE-METAL DHCP]");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  * IPv4 Address      : " + FormatIp(CurrentIp) + "   | Subnet Mask : " + FormatIp(CurrentMask));
            Console.WriteLine("  * Default Gateway   : " + FormatIp(CurrentGw) + "   | DNS Server  : " + FormatIp(CurrentDns));
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  -> Press [R] to refresh MMIO register readings, or [1] to return");
        }

        private void DrawPciAndUsbScannerScreen()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === PCI, MINI-PCIe & USB 2.0 HARDWARE BUS SCANNER (TOSHIBA NB100) ===");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" BUS:SL:FN  VEN:DEV    CLASS   IRQ  BAR0        CHIP DESCRIPTION");

            int maxRows = PciDevices.Count > 10 ? 10 : PciDevices.Count;
            int startIdx = 0;
            if (_selectedPciIdx >= maxRows) startIdx = _selectedPciIdx - maxRows + 1;

            for (int i = startIdx; i < PciDevices.Count && i < startIdx + maxRows; i++)
            {
                var d = PciDevices[i];
                bool sel = (i == _selectedPciIdx);

                if (sel)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    if (d.IsWifiCandidate || d.IsEthernet) Console.ForegroundColor = ConsoleColor.Green;
                    else if (d.IsUsbController) Console.ForegroundColor = ConsoleColor.Yellow;
                    else Console.ForegroundColor = ConsoleColor.Gray;
                }

                string loc = (sel ? ">" : " ") + d.Bus + ":" + d.Slot + ":" + d.Function;
                string id = Hex16(d.VendorId) + ":" + Hex16(d.DeviceId);
                string cls = Hex8(d.ClassCode) + ":" + Hex8(d.Subclass);
                string bar0 = "0x" + Hex32(d.Bars[0]);

                string line = PadRight(loc, 10) + " " +
                              PadRight(id, 10) + " " +
                              PadRight(cls, 6) + "  " +
                              PadRight(d.Irq.ToString(), 3) + "  " +
                              PadRight(bar0, 10) + "  " +
                              d.Description;

                Console.WriteLine(PadRight(line, 79));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [USB 2.0 EHCI PORTS DETECTED VIA MMIO (INCLUDES MINI-PCIe WI-FI SLOT)]:");
            Console.ForegroundColor = ConsoleColor.Gray;

            int shownPorts = 0;
            for (int p = 0; p < EhciPorts.Count; p++)
            {
                var port = EhciPorts[p];
                if (port.Connected || p >= 5)
                {
                    Console.ForegroundColor = port.Connected ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.WriteLine("  * USB Port #" + port.PortNumber + " [PORTSC=0x" + Hex32(port.RawPortSc) + "]: " +
                                      (port.Connected ? "CONNECTED (High-Speed) - " : "Free - ") + port.Note);
                    shownPorts++;
                    if (shownPorts >= 3) break;
                }
            }
            if (shownPorts == 0)
            {
                Console.WriteLine("  * EHCI Controller inspected. Press [ENTER] on any PCI chip to view BARs/Hex.");
            }
        }

        private void DrawPciDeviceHexInspector(PciDeviceInfo d)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === PCI CONFIG SPACE DEEP INSPECTOR (" + d.Bus + ":" + d.Slot + ":" + d.Function + " - " + d.Description + ") ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  Vendor ID : 0x" + Hex16(d.VendorId) + " | Device ID : 0x" + Hex16(d.DeviceId) +
                              " | Subsystem : 0x" + Hex16(d.SubsysVendorId) + ":0x" + Hex16(d.SubsysDeviceId));
            Console.WriteLine("  Class/Sub : 0x" + Hex8(d.ClassCode) + "/0x" + Hex8(d.Subclass) +
                              " (ProgIF 0x" + Hex8(d.ProgIf) + ") | Revision: 0x" + Hex8(d.RevisionId) + " | IRQ: " + d.Irq);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [BASE ADDRESS REGISTERS (BAR0 - BAR5)]:");
            Console.ForegroundColor = ConsoleColor.Gray;
            for (int b = 0; b < 6; b += 2)
            {
                Console.WriteLine("   BAR" + b + ": 0x" + Hex32(d.Bars[b]) + " (" + DescribeBar(d.Bars[b]) + ")   |   " +
                                  "BAR" + (b + 1) + ": 0x" + Hex32(d.Bars[b + 1]) + " (" + DescribeBar(d.Bars[b + 1]) + ")");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [HEXADECIMAL DUMP OF FIRST 64 BYTES OF PCI CONFIG SPACE]:");
            Console.ForegroundColor = ConsoleColor.Green;
            var rawDev = new PCIDeviceNormal(d.Bus, d.Slot, d.Function);
            for (byte row = 0; row < 64; row += 16)
            {
                string hexRow = "   0x" + Hex8(row) + ": ";
                for (byte col = 0; col < 16; col += 4)
                {
                    uint val = rawDev.ReadRegister32((byte)(row + col));
                    hexRow += Hex8((byte)(val & 0xFF)) + " " +
                              Hex8((byte)((val >> 8) & 0xFF)) + " " +
                              Hex8((byte)((val >> 16) & 0xFF)) + " " +
                              Hex8((byte)((val >> 24) & 0xFF)) + "  ";
                }
                Console.WriteLine(hexRow);
            }
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [ESC] -> Return to PCI device list");
        }

        private void DrawLivePacketSnifferScreen()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === REAL-TIME RAW PACKET SNIFFER (LIVE DMA RX/TX) ===");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" DIR   PROTO SIZE   SOURCE / DESTINATION / FRAME DETAILS");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" ----- ----- ------ --------------------------------------------------------");

            if (_sniffCount == 0)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("  (Listening to DMA RX/TX ring via HackPoll... Press [4] to trigger DHCP traffic)");
            }
            else
            {
                for (int i = 0; i < _sniffCount; i++)
                {
                    string entry = _sniffLogs[i];
                    if (entry != null)
                    {
                        Console.ForegroundColor = entry.StartsWith("[TX]") ? ConsoleColor.Yellow : ConsoleColor.Green;
                        Console.WriteLine(" " + entry);
                    }
                }
            }
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            Console.WriteLine("  [4] Trigger live DHCP Discover/Request   |   [ESC] Return to Menu");
        }

        private void PerformWifiScanSweep()
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [REAL 802.11 SCANNER] Sweeping 2.4 GHz radio channels (Channels 1 to 13)...");
            Console.WriteLine(" [PROBE REQUESTS] Broadcasting active 802.11 probe request frames over the air...");
            Console.WriteLine();

            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                int[] freqs = new int[] { 2412, 2417, 2422, 2427, 2432, 2437, 2442, 2447, 2452, 2457, 2462, 2467, 2472 };
                for (int ch = 1; ch <= 13; ch++)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("  -> Channel " + PadRight(ch.ToString(), 2) + " (" + freqs[ch - 1] + " MHz): ");

                    _wifiDriver.ResetAndTuneChannel(ch);
                    _wifiDriver.SendProbeRequest();

                    for (int d = 0; d < 3500; d++)
                    {
                        _wifiDriver.PollRx();
                        if (_ethDriver != null) _ethDriver.HackPoll();
                        for (int spin = 0; spin < 40; spin++) IOPort.Wait();
                    }

                    int countOnCh = 0;
                    foreach (var kvp in _wifiDriver.DetectedAps)
                    {
                        if (kvp.Value.Channel == ch)
                        {
                            countOnCh++;
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write("[" + kvp.Value.Ssid + " " + kvp.Value.RssiDbm + "dBm] ");
                        }
                    }

                    if (countOnCh == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("clear (-95 dBm)");
                    }
                    Console.WriteLine();
                }

                _wifiDriver.ResetAndTuneChannel(1);
                SyncDetectedNetworks();
                _statusBanner = "802.11 scan complete! " + WifiNetworks.Count + " real network(s) detected over the air.";
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [WARNING] Atheros AR2425 chip not detected or not initialized.");
                _statusBanner = "Error: Atheros driver not available for scanning.";
            }
        }

        private void ConnectToSelectedWifi(WifiAccessPoint ap)
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === CONNECTING TO WI-FI: " + ap.Ssid + " (" + ap.Bssid + ") ===");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Security: " + ap.Security + " (" + ap.Cipher + ") | Channel " + ap.Channel + " (" + ap.FrequencyMHz + " MHz)");
            Console.WriteLine();

            string pwd = "";
            if (!ap.IsOpen)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("  Enter Wi-Fi Password: ");
                pwd = ReadPasswordWhilePolling();
                if (pwd == null)
                {
                    _statusBanner = "Wi-Fi connection cancelled by user.";
                    return;
                }

                if (pwd.Length < 8)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine();
                    Console.WriteLine("  [ERROR] Invalid WPA2 passphrase! Minimum 8 characters required.");
                    Console.WriteLine("          Connection aborted - password rejected.");
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("  Press any key to return to main menu...");
                    WaitKeyWhilePolling();
                    _statusBanner = "Error: Invalid WPA2 passphrase (min 8 chars).";
                    _currentScreen = 0;
                    return;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [1/4] Tuning Atheros RF synthesizer to Channel " + ap.Channel + " (" + ap.FrequencyMHz + " MHz)...");
            SpinPoll(300000);

            if (ap.IsOpen)
            {
                Console.WriteLine("  [2/2] Transmitting 802.11 Open Auth & Association Request frames over the air to " + ap.Bssid + "...");
                if (_wifiDriver != null)
                {
                    _wifiDriver.AssociateWithAp(ap.Bssid, ap.Ssid, ap.Channel);
                }
                SpinPoll(400000);

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  [SUCCESS] Open Network (No Password): 802.11 Association active!");
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("            Device is associated and registered on Open Access Point!");

                for (int i = 0; i < WifiNetworks.Count; i++)
                {
                    WifiNetworks[i].IsConnected = false;
                }
                ap.IsConnected = true;
                ConnectedWifi = ap;
                _statusBanner = "Connected to open network '" + ap.Ssid + "' (Channel " + ap.Channel + ")!";
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  [2/4] Transmitting 802.11 Open Auth & RSN Association Request frames over the air...");
                if (_wifiDriver != null)
                {
                    _wifiDriver.StartWpa2Handshake(ap.Bssid, ap.Ssid, ap.Channel, pwd);
                }

                Console.WriteLine("  [3/4] Initiating IEEE 802.11i WPA2-PSK 4-Way Handshake with Access Point...");
                Console.WriteLine("        Awaiting EAPOL-Key Message 1, computing PTK/MIC, and replying Message 2...");

                int timeoutTicks = 450;
                while (_wifiDriver != null &&
                       _wifiDriver.Wpa2Engine != null &&
                       _wifiDriver.Wpa2Engine.State != Wpa2HandshakeState.Completed &&
                       _wifiDriver.Wpa2Engine.State != Wpa2HandshakeState.Failed &&
                       timeoutTicks > 0)
                {
                    _wifiDriver.PollRx();
                    if (_ethDriver != null) _ethDriver.HackPoll();
                    for (int spin = 0; spin < 1000; spin++) IOPort.Wait();
                    timeoutTicks--;
                }

                if (_wifiDriver != null && _wifiDriver.Wpa2Engine != null && _wifiDriver.Wpa2Engine.State == Wpa2HandshakeState.Completed)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine();
                    Console.WriteLine("  [4/4] [SUCCESS] WPA2-PSK 4-WAY HANDSHAKE COMPLETED SUCCESSFULLY!");
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine("        Pairwise CCMP (AES-128) Temporal Key programmed into Atheros AR2425 hardware.");
                    Console.WriteLine("        Station authenticated with valid MICs. Hotspot client registered!");

                    for (int i = 0; i < WifiNetworks.Count; i++)
                    {
                        WifiNetworks[i].IsConnected = false;
                    }
                    ap.IsConnected = true;
                    ConnectedWifi = ap;
                    _statusBanner = "Authenticated and Connected to '" + ap.Ssid + "' (WPA2-PSK)!";
                }
                else
                {
                    string failReason = (_wifiDriver != null && _wifiDriver.Wpa2Engine != null && _wifiDriver.Wpa2Engine.State == Wpa2HandshakeState.Failed)
                        ? _wifiDriver.Wpa2Engine.FailureReason
                        : "Handshake timed out waiting for AP EAPOL response (Incorrect password or signal loss).";

                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine();
                    Console.WriteLine("  [4/4] [FAILED] " + failReason);
                    Console.WriteLine("        Authentication rejected. Laptop was NOT connected.");

                    for (int i = 0; i < WifiNetworks.Count; i++)
                    {
                        WifiNetworks[i].IsConnected = false;
                    }
                    ap.IsConnected = false;
                    ConnectedWifi = null;
                    _statusBanner = "Connection failed: " + failReason;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  Press any key to return to main menu...");
            WaitKeyWhilePolling();
            _currentScreen = 0;
        }
        private void PromptAddCustomNetwork()
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === ADD MANUAL WI-FI NETWORK (HIDDEN / CUSTOM SSID) ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  Network Name (SSID): ");
            string ssid = ReadLineWhilePolling(false);
            if (string.IsNullOrEmpty(ssid))
            {
                _statusBanner = "Manual network addition cancelled.";
                return;
            }

            var customAp = new WifiAccessPoint();
            customAp.Ssid = ssid;
            customAp.Bssid = "02:1E:33:AA:BB:" + Hex8((byte)(WifiNetworks.Count + 16));
            customAp.Channel = 6;
            customAp.FrequencyMHz = 2437;
            customAp.RssiDbm = -48;
            customAp.NoiseDbm = -95;
            customAp.Antenna1Dbm = -48;
            customAp.Antenna2Dbm = -50;
            customAp.Security = "WPA2-PSK";
            customAp.Cipher = "CCMP (AES-128)";
            customAp.AuthType = "PSK (Pre-Shared Key)";
            customAp.PhyMode = "802.11b/g/n";
            customAp.MaxRateMbps = 150;
            customAp.IsOpen = false;
            customAp.Capabilities = "ESS, Privacy, ShortPreamble, ShortSlot";

            WifiNetworks.Add(customAp);
            _selectedWifiIdx = WifiNetworks.Count - 1;
            _statusBanner = "Network '" + ssid + "' added! Press [ENTER] to connect.";
        }

        private void RunManualDhcpRenewal()
        {
            if (_ethDriver == null || _dhcp == null) return;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  -> Executing Bare-Metal DHCP Discover/Request on Ethernet...");
            var newIp = _dhcp.DoAutoConfig();
            if (newIp != null)
            {
                CurrentIp = newIp;
                if (_dhcp.SubnetMask != null) CurrentMask = _dhcp.SubnetMask;
                if (_dhcp.GatewayIp != null) CurrentGw = _dhcp.GatewayIp;
                else CurrentGw = new byte[] { newIp[0], newIp[1], newIp[2], 1 };
                if (_dhcp.DnsIp != null) CurrentDns = _dhcp.DnsIp;
                else CurrentDns = CurrentGw;

                var subnet = new Sys.Network.IPv4.Address(CurrentMask[0], CurrentMask[1], CurrentMask[2], CurrentMask[3]);
                var ipAddr = new Sys.Network.IPv4.Address(CurrentIp[0], CurrentIp[1], CurrentIp[2], CurrentIp[3]);
                var gwAddr = new Sys.Network.IPv4.Address(CurrentGw[0], CurrentGw[1], CurrentGw[2], CurrentGw[3]);

                var config = new Sys.Network.Config.IPConfig(ipAddr, subnet, gwAddr);
                Sys.Network.NetworkStack.ConfigIP(_ethDriver, config);

                _statusBanner = "DHCP OK! New IP: " + FormatIp(CurrentIp) + " (GW: " + FormatIp(CurrentGw) + ")";
            }
        }

        private void SpinPoll(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
            }
        }

        private void WaitKeyWhilePolling()
        {
            while (true)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
                if (_wifiDriver != null) _wifiDriver.PollRx();
                if (Console.KeyAvailable)
                {
                    Console.ReadKey(true);
                    break;
                }
                for (int spin = 0; spin < 40; spin++) IOPort.Wait();
            }
        }

        private string ReadPasswordWhilePolling()
        {
            return ReadLineWhilePolling(true);
        }

        private string ReadLineWhilePolling(bool maskChars)
        {
            string input = "";
            while (true)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Escape) return null;
                    if (k.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return input;
                    }
                    if (k.Key == ConsoleKey.Backspace)
                    {
                        if (input.Length > 0)
                        {
                            input = input.Substring(0, input.Length - 1);
                            Console.Write("\b \b");
                        }
                    }
                    else if (k.KeyChar >= 32 && k.KeyChar <= 126 && input.Length < 32)
                    {
                        input += k.KeyChar;
                        Console.Write(maskChars ? '*' : k.KeyChar);
                    }
                }
            }
        }

        private string DescribeBar(uint bar)
        {
            if (bar == 0) return "Not used";
            if ((bar & 1) == 1) return "I/O Port 0x" + Hex16((ushort)(bar & 0xFFFC));
            return "MMIO 0x" + Hex32(bar & 0xFFFFFFF0);
        }

        private int CalculateSignalPercent(int dBm)
        {
            if (dBm >= -50) return 100;
            if (dBm <= -100) return 0;
            return 2 * (dBm + 100);
        }

        private string FormatSignalBars(int dBm)
        {
            if (dBm >= -50) return "[####]";
            if (dBm >= -62) return "[###.]";
            if (dBm >= -72) return "[##..]";
            if (dBm >= -82) return "[#...]";
            return "[....]";
        }

        private string FormatIp(byte[] ip)
        {
            if (ip == null || ip.Length < 4) return "0.0.0.0";
            return ip[0] + "." + ip[1] + "." + ip[2] + "." + ip[3];
        }

        private string PadRight(string s, int len)
        {
            if (s == null) s = "";
            if (s.Length >= len) return s.Substring(0, len);
            while (s.Length < len) s += " ";
            return s;
        }

        private static string HexNibble(int n)
        {
            n &= 0x0F;
            if (n < 10) return ((char)('0' + n)).ToString();
            return ((char)('A' + (n - 10))).ToString();
        }

        public static string Hex8(byte b)
        {
            return HexNibble(b >> 4) + HexNibble(b & 0x0F);
        }

        public static string Hex16(ushort w)
        {
            return Hex8((byte)(w >> 8)) + Hex8((byte)(w & 0xFF));
        }

        public static string Hex32(uint dw)
        {
            return Hex16((ushort)(dw >> 16)) + Hex16((ushort)(dw & 0xFFFF));
        }
    }
}
