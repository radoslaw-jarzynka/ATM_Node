using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Packet;
using System.IO;
using AddressLibrary;
using System.Collections;

namespace Nodix {
    public partial class Nodix : Form {
        delegate void SetTextCallback(string text);

        public class Route {
            public Address destAddr;
            public int bandwidth;
            public int port;
            public List<int> VPIList;

            public Route(Address addr, int band, int port, List<int> VPIList) {
                destAddr = addr;
                bandwidth = band;
                this.port = port;
                this.VPIList = VPIList;
            }
        }

        //otrzymany i wysyłany pakiets
        private Packet.ATMPacket receivedPacket;
        private Packet.ATMPacket processedPacket;

        //unikalny numer danego węzła
        //public int nodeNumber { get; set; }
        private bool isNodeAddressSet;

        private bool isNameSet;

        public Address myAddress {get; set;}

        //kolejka pakietów odebranych z chmury - concurrentQueue jest thread-safe, zwykła queue nie
        public ConcurrentQueue<Packet.ATMPacket> queuedReceivedPackets = new ConcurrentQueue<Packet.ATMPacket>();

        //dane chmury
        private IPAddress controlCloudAddress;        //Adres na którym chmura nasłuchuje
        private Int32 controlCloudPort;           //port chmury
        private IPEndPoint controlCloudEndPoint;
        private Socket controlCloudSocket;

        private Thread controlReceiveThread;     //wątek służący do odbierania połączeń
        private Thread controlSendThread;        // analogicznie - do wysyłania

        private Queue _whatToSendQueue;
        public Queue whatToSendQueue;
        public bool connect = false;
        private eLReMix LRM;
        //dane chmury
        private IPAddress cloudAddress;        //Adres na którym chmura nasłuchuje
        private Int32 cloudPort;           //port chmury
        //dane zarządcy
        private IPAddress managerAddress;        //Adres na którym chmura nasłuchuje
        private Int32 managerPort;           //port chmury

        private IPEndPoint cloudEndPoint;
        public IPEndPoint managerEndPoint {get; private set;}

        private NetworkStream networkStream; // stream dla chmury transportowej
        //strumienie
        private NetworkStream controlNetworkStream; //dla sterowania

        private Socket cloudSocket;
        public Socket managerSocket { get; private set; }

        private Thread receiveThread;     //wątek służący do odbierania połączeń
        private Thread sendThread;        // analogicznie - do wysyłania

        public bool isRunning { get; private set; }     //info czy klient chodzi - dla zarządcy

        public bool isConnectedToControlCloud { get; private set; }
        public bool isConnectedToCloud { get; private set; } // czy połączony z chmurą?
        public bool isConnectedToManager { get; set; } // czy połączony z zarządcą?

        public bool isLoggedToManager { get; set; } // czy zalogowany w zarządcy?
        private int exceptionCount;
        public bool isDisconnect;

        //agent zarządzania
        private Agentix agent;
        
        public List<Route> routeList;
        // tablica kierowania
        // UWAGA!

        // w momencie gdy chcemy ustawić połączenie VP (jak na slajdzie 22 z wykładów TSST to wartość VCI w strukturze 
        // trzeba ustawić na 0. Musiałem zaimplementować coś w stylu tej kreski co jest w [a,-] -> [b,-], a skoro
        // pole VCI ma 16 bitów to ten int zgodnie z założeniami może przybierać wartośći od 1 do 65536, 0 jest poza
        //jego zasięgiem

        //
        private Dictionary<PortVPIVCI, PortVPIVCI> VCArray = new Dictionary<PortVPIVCI,PortVPIVCI>(new PortVPIVCIComparer());

        public Nodix() {
            exceptionCount = 0;
            isNameSet = false;
            InitializeComponent();
            isNodeAddressSet = false;
            isConnectedToControlCloud = false;
            isConnectedToCloud = false;
            isLoggedToManager = false;
            isDisconnect = false;
            routeList = new List<Route>();
            _whatToSendQueue = new Queue();
            whatToSendQueue = Queue.Synchronized(_whatToSendQueue);
            (new Thread(new ThreadStart(() => {
                Thread.Sleep(1500);
                if (connect) {
                    connectToCloud(this, new EventArgs());
                    conToCloudButton_Click(this, new EventArgs());
                    connect = false;
                }
            }))).Start();
        }

        private void connectToCloud(object sender, EventArgs e) {
            if (isNodeAddressSet) {
                if (!isConnectedToCloud) {
                    if (IPAddress.TryParse(cloudIPField.Text, out cloudAddress)) {
                        SetText("IP ustawiono jako " + cloudAddress.ToString() + " \n");
                    } else {
                        SetText("Błąd podczas ustawiania IP chmury (zły format?)" + " \n");
                    }
                    if (Int32.TryParse(cloudPortField.Text, out cloudPort)) {
                        SetText("Port chmury ustawiony jako " + cloudPort.ToString() + " \n");
                    } else {
                        SetText("Błąd podczas ustawiania portu chmury (zły format?)" + " \n");
                    }

                    cloudSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                    cloudEndPoint = new IPEndPoint(cloudAddress, cloudPort);
                    try {
                        cloudSocket.Connect(cloudEndPoint);
                        isConnectedToCloud = true;
                        receiveThread = new Thread(this.receiver);
                        receiveThread.IsBackground = true;
                        receiveThread.Start();
                        sendThread = new Thread(this.sender);
                        sendThread.IsBackground = true;
                        sendThread.Start();
                    } catch {
                        isConnectedToCloud = false;
                        SetText("Błąd podczas łączenia się z chmurą\n");
                        SetText("Złe IP lub port? Chmura nie działa?\n");
                    }
                } else SetText("Węzeł jest już połączony z chmurą\n");
            } else SetText("Ustal numer węzła!\n");
        }

        private void connectToManager(object sender, EventArgs e) {
            if (isNodeAddressSet) {
                if (!isConnectedToManager) {
                    if (IPAddress.TryParse(managerIPField.Text, out managerAddress)) {
                        log.AppendText("IP zarządcy ustawione jako " + managerAddress.ToString() + " \n");
                    } else {
                        log.AppendText("Błąd podczas ustawiania IP zarządcy\n");
                    }
                    if (Int32.TryParse(managerPortField.Text, out managerPort)) {
                        log.AppendText("Port zarządcy ustawiony jako " + managerPort.ToString() + " \n");
                    } else {
                        log.AppendText("Błąd podczas ustawiania portu zarządcy\n");
                    }

                    managerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

                    managerEndPoint = new IPEndPoint(managerAddress, managerPort);
                    try {
                        managerSocket.Connect(managerEndPoint);
                        isConnectedToManager = true;
                        agent = new Agentix(this);
                        agent.writeThread.Start();
                        agent.writeThread.IsBackground = true;
                        agent.readThread.Start();
                        agent.readThread.IsBackground = true;
                        agent.sendLoginT = true;
                    } catch (SocketException) {
                        isConnectedToManager = false;
                        log.AppendText("Błąd podczas łączenia się z zarządcą!\n");
                        log.AppendText("Złe IP lub port? Zarządca nie działa?\n");
                    }
                } else SetText("Już jestem połączony z zarządcą!\n");
            } else SetText("Ustal numer węzła!\n");
            
        }

        private void receiver() {
            if (networkStream == null) {
                networkStream = new NetworkStream(cloudSocket);
                
                //tworzy string 'Node ' i tu jego numer
                String welcomeString = "Node " + ((myAddress.subnet*100)+myAddress.host) + " " + myAddress.ToString();
                //tworzy tablicę bajtów z tego stringa
                byte[] welcomeStringBytes = AAL.GetBytesFromString(welcomeString);
                //wysyła tą tablicę bajtów streamem
                networkStream.Write(welcomeStringBytes, 0, welcomeStringBytes.Length);
            }
            BinaryFormatter bf = new BinaryFormatter();
            try {
                receivedPacket = (Packet.ATMPacket)bf.Deserialize(networkStream);
                if (receivedPacket.VPI == -1 && receivedPacket.VCI == -1) {
                    LRM.OdczytajATM(receivedPacket);
                }
                else queuedReceivedPackets.Enqueue(receivedPacket);

                //to może nie działać. Sprawdzi się jeszcze
                /*if (!sendThread.IsAlive) {
                    sendThread = new Thread(this.sender);
                    sendThread.IsBackground = true;
                    sendThread.Start();
                }*/
                    receiver();
            } catch (Exception e) {
                if (isDisconnect) { SetText("Rozłączam się z chmurą!\n"); isDisconnect = false; networkStream = null; }
                else { SetText("Coś poszło nie tak : " + e.Message + "\n"); }
            }
        }

        public void SetText(string text) {
            // InvokeRequired required compares the thread ID of the 
            // calling thread to the thread ID of the creating thread. 
            // If these threads are different, it returns true. 
            if (this.log.InvokeRequired) {
                SetTextCallback d = new SetTextCallback(SetText);
                this.Invoke(d, new object[] { text });
            }
            else {
                try {
                    this.log.AppendText(text);
                } catch { }
            }
        }

        private void sender() {
            while(isConnectedToCloud) {
            if (queuedReceivedPackets.Count != 0)
            {
                if (queuedReceivedPackets.TryDequeue(out processedPacket))
                {
                    if (processedPacket != null)
                    {
                        PortVPIVCI VCConKey = new PortVPIVCI();
                        PortVPIVCI VPConKey = new PortVPIVCI();
                        PortVPIVCI value = new PortVPIVCI();
                        VCConKey.port = processedPacket.port;
                        VCConKey.VPI = processedPacket.VPI;
                        VCConKey.VCI = processedPacket.VCI;
                        VPConKey.port = processedPacket.port;
                        VPConKey.VPI = processedPacket.VPI;
                        VPConKey.VCI = 0;
                        NetworkStream networkStream = new NetworkStream(cloudSocket);
                        if (processedPacket.VPI == -1 && processedPacket.VCI == -1)
                        {
                            SetText("Wysyłam pakiet sygnalizacyjny na porcie " + processedPacket.port + "\n");
                            BinaryFormatter bformatter = new BinaryFormatter();
                            bformatter.Serialize(networkStream, processedPacket);
                            networkStream.Close();
                        }
                        else
                        {
                            if (VCArray.ContainsKey(VCConKey))
                            {
                                if (VCArray.TryGetValue(VCConKey, out value))
                                {
                                    SetText("Przekierowywanie [" + processedPacket.port + ";" + processedPacket.VPI + ";" + processedPacket.VCI + "]->[" + value.port + ";" + value.VPI + ";" + value.VCI + "]\n");
                                    processedPacket.VPI = value.VPI;
                                    processedPacket.VCI = value.VCI;
                                    processedPacket.port = value.port;
                                    BinaryFormatter bformatter = new BinaryFormatter();
                                    bformatter.Serialize(networkStream, processedPacket);
                                    networkStream.Close();
                                }
                                else
                                {
                                    SetText("Coś poszło nie tak przy przepisywaniu wartości VPI i VCI z VCArray\n");
                                }
                            }
                            else if (VCArray.ContainsKey(VPConKey))
                            {
                                if (VCArray.TryGetValue(VPConKey, out value))
                                {

                                    SetText("Przekierowywanie [" + processedPacket.port + ";" + processedPacket.VPI + ";" + processedPacket.VCI + "]->[" + value.port + ";" + value.VPI + ";" + processedPacket.VCI + "]\n");
                                    processedPacket.VPI = value.VPI;
                                    processedPacket.port = value.port;
                                    // VCI bez zmian
                                    BinaryFormatter bformatter = new BinaryFormatter();
                                    bformatter.Serialize(networkStream, processedPacket);
                                    networkStream.Close();
                                }
                                else
                                {
                                    SetText("Coś poszło nie tak przy przepisywaniu wartości VPI i VCI z VCArray\n");
                                }
                            }
                            else
                            {
                                SetText("Pakiet stracony - brak odpowiedniego wpisu w tablicy\n");
                            }
                        }

                    }
                }
            }
            Thread.Sleep(50);
            }
        }

        //Dodaje pozycję do VCArray, pobiera dwa obiekty PortVPIVCI
        //WAŻNE - dodaje wpis 'w obie strony'
        public void addEntry(PortVPIVCI key, PortVPIVCI value) {
            if (VCArray.ContainsKey(key))
            {
                PortVPIVCI temp;
                SetText("Zmieniam stary klucz VCArray na [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI +"]\n");
                SetText("Zmieniam stary klucz VCArray na [" + value.port + ";" + value.VPI + ";" + value.VCI + "] -> [" + key.port + ";" + key.VPI + ";" + key.VCI + "]\n");
                VCArray.TryGetValue(key, out temp);
                if (temp != null) {
                    VCArray.Remove(temp);
                }
                VCArray.Remove(key);
                VCArray.Add(key, value);
                VCArray.Add(value, key);
            }
            else
            {
                SetText("Dodaję wpis [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI + "]\n");
                SetText("Dodaję wpis [" + value.port + ";" + value.VPI + ";" + value.VCI + "] -> [" + key.port + ";" + key.VPI + ";" + key.VCI + "]\n");
                VCArray.Add(key, value);
                VCArray.Add(value, key);
            }
        }

        //Dodaje pozycję do VCArray, pobiera inty jako poszczególne wartości
        //WAŻNE - dodaje wpis 'w obie strony'
        //jeśli taki wpis już jest - usuwa stary wpis (też w obie strony) i go zastępuje
        public void addEntry(int keyPort, int keyVPI, int keyVCI, int valuePort, int valueVPI, int valueVCI) {
            PortVPIVCI key = new PortVPIVCI(keyPort, keyVPI, keyVCI);
            PortVPIVCI value = new PortVPIVCI(valuePort, valueVPI, valueVCI);
            if (VCArray.ContainsKey(key)) {
                PortVPIVCI temp;
                SetText("Zmieniam stary klucz VCArray na [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI +"]\n");
                SetText("Zmieniam stary klucz VCArray na [" + value.port + ";" + value.VPI + ";" + value.VCI + "] -> [" + key.port + ";" + key.VPI + ";" + key.VCI + "]\n");
                VCArray.TryGetValue(key, out temp);
                if (temp != null) {
                    VCArray.Remove(temp);
                }
                VCArray.Remove(key);
                VCArray.Add(key, value);
                VCArray.Add(value, key);
            }
            else {
                SetText("Dodaję wpis [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI +"]\n");
                SetText("Dodaję wpis [" + value.port + ";" + value.VPI + ";" + value.VCI + "] -> [" + key.port + ";" + key.VPI + ";" + key.VCI + "]\n");
                VCArray.Add(key, value);
                VCArray.Add(value, key);
            }
        }
        //dodaje wpis w JEDNĄ stronę
        public void addSingleEntry(PortVPIVCI key, PortVPIVCI value) {
            if (VCArray.ContainsKey(key)) {
                SetText("Zmieniam stary klucz VCArray na [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI + "]\n");
                VCArray.Remove(key);
                VCArray.Add(key, value);
            } else {
                SetText("Dodaję wpis [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI + "]\n");
                VCArray.Add(key, value);
            }
        }

        //Dodaje pozycję do VCArray, pobiera inty jako poszczególne wartości
        //WAŻNE - dodaje wpis 'w jedna strone'
        public void addSingleEntry(int keyPort, int keyVPI, int keyVCI, int valuePort, int valueVPI, int valueVCI) {
            PortVPIVCI key = new PortVPIVCI(keyPort, keyVPI, keyVCI);
            PortVPIVCI value = new PortVPIVCI(valuePort, valueVPI, valueVCI);
            if (VCArray.ContainsKey(key)) {
                SetText("Zmieniam stary klucz VCArray na [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI + "]\n");
                VCArray.Remove(key);
                VCArray.Add(key, value);
            } else {
                SetText("Dodaję klucz VCArray na [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + value.port + ";" + value.VPI + ";" + value.VCI + "]\n");
                VCArray.Add(key, value);
            }
        }

        //usuwa pojedynczy wpis
        public void removeSingleEntry(PortVPIVCI key) {
            if (VCArray.ContainsKey(key)) {
                PortVPIVCI temp = null;
                VCArray.TryGetValue(key, out temp);
                SetText("Usuwam klucz w VCArray [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "]\n");
                VCArray.Remove(key);
            }
            else SetText("Nie ma takiego klucza\n");
        }
        //usuwa oba wpisy, jak się nie da to usuwa tylko jeden
        public void removeEntry(PortVPIVCI key) {
            if (VCArray.ContainsKey(key) && VCArray.ContainsValue(key)) {
                PortVPIVCI temp = null;
                VCArray.TryGetValue(key, out temp);
                if (temp != null) {
                    SetText("Usuwam klucz w VCArray [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "] -> [" + key.port + ";" + key.VPI + ";" + key.VCI + "]\n");
                    VCArray.Remove(temp);
                }
                SetText("Usuwam klucz w VCArray [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "]\n");
                VCArray.Remove(key);
            } else removeSingleEntry(key);
        }

        //usuwa pojedynczy wpis
        public void removeSingleEntry(int keyPort, int keyVPI, int keyVCI) {
            PortVPIVCI key = new PortVPIVCI(keyPort, keyVPI, keyVCI);
            if (VCArray.ContainsKey(key)) {
                PortVPIVCI temp = null;
                VCArray.TryGetValue(key, out temp);
                SetText("Usuwam klucz w VCArray [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "]\n");
                VCArray.Remove(key);
            }
            else SetText("Nie ma takiego klucza\n");
        }

        //usuwa oba wpisy, jak się nie uda to tylko jeden
        public void removeEntry(int keyPort, int keyVPI, int keyVCI) {
            PortVPIVCI key = new PortVPIVCI(keyPort, keyVPI, keyVCI);
            if (VCArray.ContainsKey(key)) {
                PortVPIVCI temp = null;
                VCArray.TryGetValue(key, out temp);
                if (temp != null) {
                    SetText("Usuwam klucz w VCArray [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "] -> [" + key.port + ";" + key.VPI + ";" + key.VCI + "]\n");
                    VCArray.Remove(temp);
                }
                SetText("Usuwam klucz w VCArray [" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "]\n");
                VCArray.Remove(key);
            } else removeSingleEntry(key);
        }

        public void clearTable() {
            VCArray = new Dictionary<PortVPIVCI, PortVPIVCI>(new PortVPIVCIComparer());
            SetText("Czyszczę tablicę kierowania\n");
        }

        private void setNodeNumber_Click(object sender, EventArgs e) {
            try {
                int nodeAddressNetwork = int.Parse(NodeNetworkNumberField.Text);
                int nodeAddressSubnet = int.Parse(NodeSubnetworkNumberField.Text);
                int nodeAddressHost = int.Parse(NodeHostNumberField.Text);
                isNodeAddressSet = true;
                myAddress = new Address(nodeAddressNetwork, nodeAddressSubnet, nodeAddressHost);
                SetText("Numer węzła ustawiony jako " + myAddress.ToString() + "\n");
                Nodix.ActiveForm.Text = "Nodix " + myAddress.ToString();
            } catch {
                isNodeAddressSet = false;
                SetText("Błędne dane wejściowe\n");
                Nodix.ActiveForm.Text = "Nodix";
            }
        }

        private void addEntryButton_Click(object sender, EventArgs e) {
            try {
                PortVPIVCI inValue = new PortVPIVCI(int.Parse(inPortTextBox.Text), int.Parse(inVPITextBox.Text), int.Parse(inVCITextBox.Text));
                PortVPIVCI outValue = new PortVPIVCI(int.Parse(outPortTextBox.Text), int.Parse(outVPITextBox.Text), int.Parse(outVCITextBox.Text));
                addSingleEntry(inValue, outValue);
            } catch {}
            inPortTextBox.Clear();
            inVPITextBox.Clear();
            inVCITextBox.Clear();
            outPortTextBox.Clear();
            outVPITextBox.Clear();
            outVCITextBox.Clear();
        }

        private void chooseTextFile_Click(object sender, EventArgs e) {
            string path;
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == DialogResult.OK) {
                path = openFileDialog.FileName;
                readConfig(path, true);
            }
        }

        public void readConfig(String nAddr) {
            try {
                myAddress = Address.Parse(nAddr);
                isNodeAddressSet = true;
                NodeNetworkNumberField.Text = String.Empty + myAddress.network;
                NodeSubnetworkNumberField.Text = String.Empty + myAddress.subnet;
                NodeHostNumberField.Text = String.Empty + myAddress.host;
                SetText("Ustalam adres węzła jako " + myAddress.ToString() + "\n");
                String path = "config" + nAddr + ".txt";
                using (StreamReader sr = new StreamReader(path)) {
                    string[] lines = System.IO.File.ReadAllLines(path);
                    foreach (String line in lines) {
                        String[] command = line.Split(' ');
                        if (command[0] == "ADD") {
                            try {
                                addSingleEntry(int.Parse(command[1]), int.Parse(command[2]), int.Parse(command[3]),
                                    int.Parse(command[4]), int.Parse(command[5]), int.Parse(command[6]));
                            } catch (IndexOutOfRangeException) {
                                SetText("Komenda została niepoprawnie sformułowana (za mało parametrów)\n");
                            }
                        } else if (command[0] == "CLEAR") {
                            clearTable();
                        } else if (command[0] == "ADD_ROUTE") {
                            Address adr;
                            int port;
                            int band;
                            if (int.TryParse(command[1], out port)) {
                                if (Address.TryParse(command[2], out adr)) {
                                    if (int.TryParse(command[3], out band)) {
                                        List<int> _VPIList = new List<int>();
                                        for (int i = 4; i < command.Length; i++) {
                                            int vpi;
                                            if (int.TryParse(command[i], out vpi)) {
                                                _VPIList.Add(vpi);
                                            }
                                        }
                                        routeList.Add(new Route(adr, band, port, _VPIList));
                                        SetText("Dodaję ścieżkę do " + adr.ToString() + " o przepustowości " + band + " Mbit/s na porcie " + port + "\n");
                                        String _VPIListString = String.Empty;
                                        foreach (int i in _VPIList) {
                                            _VPIListString += i + " ";
                                        }
                                        SetText("VPaths na tym łączu to " + _VPIListString + "\n");
                                    } else SetText("Zły format danych\n");
                                }else SetText("Zły format danych\n");
                            }else SetText("Zły format danych\n");
                        }
                    }
                }
            } catch (Exception exc) {
                SetText("Błąd podczas konfigurowania pliku konfiguracyjnego\n");
                SetText(exc.Message + "\n");
            }
        }

        public void readConfig(String path, bool justToOverload) {
            try {
                //myAddress = Address.Parse(nAddr);
                //isNodeAddressSet = true;
                //NodeNetworkNumberField.Text = String.Empty + myAddress.network;
                //NodeSubnetworkNumberField.Text = String.Empty + myAddress.subnet;
                //NodeHostNumberField.Text = String.Empty + myAddress.host;
                SetText("Wczytuje plik konfiguracyjny z " + path + "\n");
                //String path = "config" + nAddr + ".txt";
                using (StreamReader sr = new StreamReader(path)) {
                    string[] lines = System.IO.File.ReadAllLines(path);
                    foreach (String line in lines) {
                        String[] command = line.Split(' ');
                        if (command[0] == "ADD") {
                            try {
                                addSingleEntry(int.Parse(command[1]), int.Parse(command[2]), int.Parse(command[3]),
                                    int.Parse(command[4]), int.Parse(command[5]), int.Parse(command[6]));
                            } catch (IndexOutOfRangeException) {
                                SetText("Komenda została niepoprawnie sformułowana (za mało parametrów)\n");
                            }
                        } else if (command[0] == "CLEAR") {
                            clearTable();
                        } else if (command[0] == "ADD_ROUTE") {
                            Address adr;
                            int port;
                            int band;
                            if (int.TryParse(command[1], out port)) {
                                if (Address.TryParse(command[2], out adr)) {
                                    if (int.TryParse(command[3], out band)) {
                                        List<int> _VPIList = new List<int>();
                                        for (int i = 4; i < command.Length; i++) {
                                            int vpi;
                                            if (int.TryParse(command[i], out vpi)) {
                                                _VPIList.Add(vpi);
                                            }
                                        }
                                        routeList.Add(new Route(adr, band, port, _VPIList));
                                        SetText("Dodaję ścieżkę do " + adr.ToString() + " o przepustowości " + band + " Mbit/s na porcie " + port + "\n");
                                        String _VPIListString = String.Empty;
                                        foreach (int i in _VPIList) {
                                            _VPIListString += i + " ";
                                        }
                                        SetText("VPaths na tym łączu to " + _VPIListString + "\n");
                                    } else SetText("Zły format danych\n");
                                } else SetText("Zły format danych\n");
                            } else SetText("Zły format danych\n");
                        } else if (command[0] == "SET_ADDR") {
                            try{
                                myAddress = new Address(int.Parse(command[1]), int.Parse(command[2]), int.Parse(command[3]));
                                isNodeAddressSet = true;
                                NodeNetworkNumberField.Text = String.Empty + myAddress.network;
                                NodeSubnetworkNumberField.Text = String.Empty + myAddress.subnet;
                                NodeHostNumberField.Text = String.Empty + myAddress.host;
                                SetText("Ustalam adres węzła jako " + myAddress.ToString() + "\n");
                            } catch {
                                SetText("komenda ustalenia adresu została niepoprawnie sformułowana");
                            }
                        }
                    }
                }
            } catch (Exception exc) {
                SetText("Błąd podczas konfigurowania pliku konfiguracyjnego\n");
                SetText(exc.Message + "\n");
            }
        }

        private void disconnectButton_Click(object sender, EventArgs e) {
            isDisconnect = true;
            isConnectedToCloud = false;
            isConnectedToManager = false;
            if (cloudSocket != null) cloudSocket.Close();
            if (managerSocket != null) managerSocket.Close();
        }

        private void saveConfigButton_Click(object sender, EventArgs e) {
            saveConfig();
        }

        private void saveConfig() {
            if (myAddress != null) {
                List<String> lines = new List<String>();
                foreach (PortVPIVCI key in VCArray.Keys) {
                    PortVPIVCI value;
                    if (VCArray.TryGetValue(key, out value)) lines.Add("ADD " + key.port + " " + key.VPI + " " + key.VCI +
                                                                        " " + value.port + " " + value.VPI + " " + value.VCI);
                }
                
                foreach (Route rt in routeList) {
                    String _vpiString = String.Empty;
                    foreach (int _vpi in rt.VPIList) {
                        _vpiString += _vpi + " ";
                    }
                    lines.Add("ADD_ROUTE " + rt.port + " " + rt.destAddr.ToString() + " " + rt.bandwidth + " " + _vpiString);
                }
                System.IO.File.WriteAllLines("config" + myAddress.ToString() + ".txt", lines);
                SetText("Zapisuję ustawienia do pliku config" + myAddress.ToString() + ".txt\n");
            } else SetText("Ustal adres węzła!\n");
        }

        private void printVCArrayButton_Click(object sender, EventArgs e) {
            foreach (PortVPIVCI key in VCArray.Keys) {
                PortVPIVCI temp;
                if (VCArray.TryGetValue(key, out temp))
                SetText("[" + key.port + ";" + key.VPI + ";" + key.VCI + "] -> [" + temp.port + ";" + temp.VPI + ";" + temp.VCI + "]\n");
            }
        }

        private void Nodix_FormClosed(object sender, FormClosedEventArgs e) {
           // if (myAddress != null) saveConfig();
        }

        /// <summary>
        /// metoda wywołana po wciśnięciu "połącz z chmurąsterowania"
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void conToCloudButton_Click(object sender, EventArgs e) {
            if (!isConnectedToControlCloud) {
                if (isNodeAddressSet) {
                    if (IPAddress.TryParse(controlCloudIPTextBox.Text, out controlCloudAddress)) {
                        SetText("IP ustawiono jako " + controlCloudAddress.ToString()+"\n");
                    } else {
                        SetText("Błąd podczas ustawiania IP chmury (zły format?)\n");
                    }
                    if (Int32.TryParse(controlCloudPortTextBox.Text, out controlCloudPort)) {
                        SetText("Port chmury ustawiony jako " + controlCloudPort.ToString()+"\n");
                    } else {
                        SetText("Błąd podczas ustawiania portu chmury (zły format?)\n");
                    }

                    controlCloudSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    controlCloudEndPoint = new IPEndPoint(controlCloudAddress, controlCloudPort);
                    try {
                        controlCloudSocket.Connect(controlCloudEndPoint);
                        isConnectedToControlCloud = true;
                        controlNetworkStream = new NetworkStream(controlCloudSocket);
                        List<String> _welcArr = new List<String>();
                        _welcArr.Add("HELLO");
                        SPacket welcomePacket = new SPacket(myAddress.ToString(), new Address(0, 0, 0).ToString(), _welcArr);
                        whatToSendQueue.Enqueue(welcomePacket);
                        //whatToSendQueue.Enqueue("HELLO " + myAddr);
                        controlReceiveThread = new Thread(this.controlReceiver);
                        controlReceiveThread.IsBackground = true;
                        controlReceiveThread.Start();
                        controlSendThread = new Thread(this.controlSender);
                        controlSendThread.IsBackground = true;
                        controlSendThread.Start();
                        //conToCloudButton.Text = "Rozłącz";
                        LRM = new eLReMix(this);
                        SetText("Połączono!\n");
                        exceptionCount = 0;
                    } catch (SocketException) {
                        isConnectedToControlCloud = false;
                        SetText("Błąd podczas łączenia się z chmurą\n");
                        SetText("Złe IP lub port? Chmura nie działa?\n");
                    }
                } else {
                    SetText("Wprowadź numery sieci i podsieci\n");
                }
            } else {
                isConnectedToControlCloud = false;
                conToCloudButton.Text = "Połącz";
                SetText("Rozłączono!\n");
                if (controlCloudSocket != null) controlCloudSocket.Close();
            }
        }
        /// <summary>
        /// wątek odbierający wiadomości z chmury
        /// </summary>
        public void controlReceiver() {
            while (isConnectedToControlCloud) {
                BinaryFormatter bf = new BinaryFormatter();
                try {
                    SPacket receivedPacket = (Packet.SPacket)bf.Deserialize(controlNetworkStream);
                    //_msg = reader.ReadLine();
                    SetText("Odczytano:\n" + receivedPacket.ToString() + "\n");
                    LRM.OdczytajS(receivedPacket);
                } catch {
                    SetText("WUT");
                    if (++exceptionCount == 5) {
                        this.Invoke((MethodInvoker)delegate() {
                            isConnectedToControlCloud = false;
                            conToCloudButton.Text = "Połącz";
                            SetText("Rozłączono!");
                            if (controlCloudSocket != null) controlCloudSocket.Close();
                        });
                    }
                }
            }
        }
        /// <summary>
        /// wątek wysyłający wiadomości do chmury
        /// </summary>
        public void controlSender() {
            while (isConnectedToControlCloud) {
                //jeśli coś jest w kolejce - zdejmij i wyślij
                if (whatToSendQueue.Count != 0) {
                    SPacket _pck = (SPacket)whatToSendQueue.Dequeue();
                    BinaryFormatter bformatter = new BinaryFormatter();
                    bformatter.Serialize(controlNetworkStream, _pck);
                    controlNetworkStream.Flush();
                    String[] _argsToShow = _pck.getParames().ToArray();
                    String argsToShow = "";
                    foreach (String str in _argsToShow) {
                        argsToShow += str + " ";
                    }
                    SetText("Wysłano: " + _pck.getSrc() + ":" + _pck.getDest() + ":" + argsToShow + "\n");
                    Thread.Sleep(50);
                }
            }
        }
    private void Nodix_Paint(object sender, EventArgs e) {
        if (myAddress != null) {
            Nodix.ActiveForm.Text = "Nodix " + myAddress.ToString();
            isNameSet = true;
            }
        }

    private void Nodix_MouseEnter(object sender, EventArgs e)
    {
        if (myAddress != null && isNameSet != true)
        {
            Nodix.ActiveForm.Text = "Nodix " + myAddress.ToString();
            isNameSet = true;
        }
    }
    }

    class Agentix {
        StreamReader read = null;
        StreamWriter write = null;
        NetworkStream netstream = null;
        Nodix parent;
        public Thread writeThread;
        public Thread readThread;
        public bool sendLoginT;

        public Agentix(Nodix parent) {
            this.parent = parent;
            netstream = new NetworkStream(parent.managerSocket);
            read = new StreamReader(netstream);
            write = new StreamWriter(netstream);
            readThread = new Thread(reader);
            writeThread = new Thread(writer);
        }
        //Funkcja odpowiedzialna za odbieraie danych od serwera
        //wykonywana w osobnym watąku
        private void reader() {

            String odp;
            Char[] delimitter = { ' ' };
            String[] slowa;
            while (parent.isConnectedToManager) {
                try {
                    odp = read.ReadLine();
                    slowa = odp.Split(delimitter, StringSplitOptions.RemoveEmptyEntries);
                    if (slowa[0] == "ADD") {
                        //dodawanie wpisu
                        int p1, vc1, vp1, p2, vc2, vp2;
                        if (slowa.Length != 7) {
                            parent.SetText("Zła liczba parametrów w ADD: " + slowa.Length + "\n");
                        } else {

                            p1 = int.Parse(slowa[1]);
                            vp1 = int.Parse(slowa[2]);
                            vc1 = int.Parse(slowa[3]);
                            p2 = int.Parse(slowa[4]);
                            vp2 = int.Parse(slowa[5]);
                            vc2 = int.Parse(slowa[6]);
                            parent.addSingleEntry(p1, vp1, vc1, p2, vp2, vc2);
                        }
                    } else if (slowa[0] == "DELETE") {
                        //usuwanie jednego wpisu
                        int p1, vc1, vp1, p2, vc2, vp2;
                        if (slowa.Length != 7) {
                            parent.SetText("Zła liczba parametrów w DELETE: " + slowa.Length + "\n");
                        } else {

                            p1 = int.Parse(slowa[1]);
                            vp1 = int.Parse(slowa[2]);
                            vc1 = int.Parse(slowa[3]);
                            p2 = int.Parse(slowa[4]);
                            vp2 = int.Parse(slowa[5]);
                            vc2 = int.Parse(slowa[6]);
                            parent.removeSingleEntry(p1, vp1, vc1);
                           // parent.removeEntry(p2, vp2, vc1);

                        }
                    } else if (slowa[0] == "CLEAR") {
                        //usuwanie wszystkich wpisów
                        parent.clearTable();
                    } else if (slowa[0] == "LOGGED") {
                        //udane logowanie
                        parent.isLoggedToManager = true;
                        //parent.addEntry(slowa[1], new PortVPIVCI( int.Parse(slowa[2]), int.Parse(slowa[3]), int.Parse(slowa[4]));
                    } else if (slowa[0] == "MSG" || slowa[0] == "DONE") {
                        parent.SetText("Wykryto komunikat od zarządcy o treści:\n");
                        parent.SetText(odp + "\n");
                    } else if (slowa[0] == "ERR") {
                        parent.SetText("Wykryto komunikat błędu o treści:");
                        foreach (String s in slowa) {
                            parent.SetText(" " + s + " ");
                        }
                        parent.SetText("\n");
                        parent.isConnectedToManager = false;
                        writeThread.Abort();
                        readThread.Abort();
                        parent.SetText("Połącz się ponownie!\n");
                    }

                } catch {
                    if (parent.isDisconnect) {
                        parent.SetText("Rozłączam się z zarządcą!\n");
                        parent.isConnectedToManager = false;
                        writeThread.Abort();
                        readThread.Abort();
                        parent.isDisconnect = false;
                    } else {
                        parent.SetText("Problem w połączeniu się z zarządcą :<\n");
                        parent.isConnectedToManager = false;
                        writeThread.Abort();
                        readThread.Abort();
                    }
                }
            }
        }

        //Funkcja przesyłająca dane do serwera
        //Wykonywana w osobnym watku
        
        private void writer() {
            while (parent.isConnectedToManager) {
                try {
                    if (sendLoginT) {
                        //write.WriteLine("LOGINT\n" + parent.myAddress.ToString());
                        write.WriteLine("LOGINT\n" + parent.myAddress.network*100+parent.myAddress.subnet*10+parent.myAddress.host);
                        write.Flush();
                        sendLoginT = false;
                    }
                } catch {
                    parent.isConnectedToManager = false;
                    writeThread.Abort();
                    readThread.Abort();
                }
            }
        }
        
    }
}
