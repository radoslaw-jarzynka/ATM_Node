using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using AddressLibrary;
using Packet;
using System.Threading;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Nodix
{
    class eLReMix
    {
        Nodix parent;//coby móc krozystać z routelist i metod
        Thread atm,s;//wątki do odbierania
        private ConcurrentQueue<Packet.ATMPacket> kolejkapyt;//kolejka zapytań pakietów ATM z vpi vci -1
        private List<Packet.ATMPacket> kolejkaodp;//kolejka odp od LRMów sąsiadów, używana w CzyZyje
        private ConcurrentQueue<Packet.SPacket> kolejkaS;//kolejka SPacketów do wątku s
        String adresLRM, adresRC, adresCC;//lokalne kopie, coby nie szukać za długo

        public eLReMix(Nodix nod)
        {
            parent = nod;
            adresLRM = nod.myAddress.ToString();
            Address temp = Address.Parse(adresLRM);
            temp.host = 0;
            adresRC = temp.ToString();
            temp.host = 1;
            adresCC = temp.ToString();
            kolejkapyt = new ConcurrentQueue<Packet.ATMPacket>();
            kolejkaodp = new List<Packet.ATMPacket>();
            kolejkaS = new ConcurrentQueue<SPacket>();
            atm = new Thread(new ThreadStart(ATMReader));
            atm.IsBackground = true;
            atm.Start();
            s = new Thread(new ThreadStart(SReader));
            s.IsBackground = true;
            s.Start();
            //logowanie
            wyslijSPacket(new SPacket(adresLRM, adresRC, "HELLO "+adresLRM));//logowanie do RC
            wyslijSPacket(new SPacket(adresLRM, adresCC, "HELLO " + adresLRM));//logowanie do RC

        }
        #region wątki czytające SPackety i ATMPackety z kolejek
        public void ATMReader()//wątek obsługujący odczytywanie pakietów z zapytaniami, te z odpowiedziami będą jakoś(jeszcze nie wiem jak) obsługiwane w CzyZyje
        {
            while (true)
            {
                if(kolejkapyt.Count == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    ATMPacket zapytanie, odpowiedz;
                    kolejkapyt.TryDequeue(out zapytanie);
                    //obsługa pakietu od innego LRMa, w tej metodzie interesują nas tylko wiadomosci z zapytaniem, odpowiedzi obsługiwane są metodą CzyZyje
                    //więc jak wykryjemy odp to idzie do drugiej kolejki już na etapie przekazania pakietu przez Nodix
                    byte[] payload = ToPayload("ZYJE");
                    odpowiedz = new Packet.ATMPacket(Packet.ATMPacket.AALType.SSM, payload, 0, 0);
                    odpowiedz.port = zapytanie.port;
                    odpowiedz.VCI = -1;
                    odpowiedz.VPI = -1;
                    parent.queuedReceivedPackets.Enqueue(odpowiedz);
                }
                catch(Exception e)
                {
                    Console.Out.WriteLine(e.Message);
                }
            }
        }
        public void SReader()
        {
            while (true)
            {
                if (kolejkaS.Count == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }
                try
                {
                    SPacket pakiet;
                    if(!kolejkaS.TryDequeue(out pakiet))
                    {
                        Thread.Sleep(100);
                        continue;//jeśli nie udało się zdejmowanie przejdź do następnego obiegu po chwili pauzy
                    }
                    String komenda = pakiet.getParames().ElementAt(0);//zczytywanie komendy
                    String nowakomenda = "";
                    if (komenda.Equals("IS_LINK_AVAILABLE"))//to jest do do wysyłania pakietu próbnego do sąsiada o zadanym adresie np. IS_ALIVE 1.2.3 dawne IS_ALIVE
                    {
                        int i2 = -1;
                        if (pakiet.getParames().Count > 1)
                            i2 = int.Parse(pakiet.getParames().ElementAt(2));
                        Address sprawdzany = Address.Parse(pakiet.getParames().ElementAt(1));
                        Tuple<Address, int> argus = new Tuple<Address, int>(sprawdzany, i2);
                        CzyZyjeRun(argus);//odpowiedzią zajmuje się metoda CzyZyje
                        continue;
                    }
                    /*else if (komenda.Equals("IS_LINK_AVAILABLE"))
                    {
                        Address sprawdzany = Address.Parse(pakiet.getParames().ElementAt(1));
                        for(int i=0; i<parent.routeList.Count;i++)
                        {
                            if(sprawdzany.Equals(parent.routeList.ElementAt(i).destAddr))//szukamy na routelist zadanego adresu
                            {
                                if(parent.routeList.ElementAt(i).bandwidth>=2)//gdy przepustowość co najmniej 2 to ok
                                {
                                    nowakomenda = "YES_AVAILABLE " + sprawdzany.ToString();
                                    break;
                                }
                                else
                                {
                                    nowakomenda = "NO_AVAILABLE " + sprawdzany.ToString();
                                    break;
                                }
                            }
                        }
                    }*/
                    else if(komenda.Equals("ADD_MAPPING"))
                    {
                       
                        if(pakiet.getParames().Count==7)
                        {
                            List<String> p = pakiet.getParames();
                            int vp1, vc1, vp2, vc2;
                            Address a1, a2;
                            a1 = Address.Parse(p.ElementAt(1));
                            vp1 = int.Parse(p.ElementAt(2));
                            vc1 = int.Parse(p.ElementAt(3));
                            a2 = Address.Parse(p.ElementAt(4));
                            vp2 = int.Parse(p.ElementAt(5));
                            vc2 = int.Parse(p.ElementAt(6));
                            int p1 = AddressToPort(a1);
                            int p2 = AddressToPort(a2);
                            parent.addSingleEntry(p1, vp1, vc1, p2, vp2, vc2);
                            ZajmijZasob(a1, a2);
                            nowakomenda = "MSG zadanie ADD wykonane";
                        }
                        else
                        {
                            nowakomenda = "ERROR ZŁA LICZBA PARAMETRÓW W ADD";
                        }
                        
                    }
                    else if(komenda.Equals("DEL_MAPPING"))
                    {
                       
                        if (pakiet.getParames().Count == 7)
                        {
                            List<String> p = pakiet.getParames();
                            int  vp1, vc1, vp2, vc2;
                            Address a1, a2;
                            a1 = Address.Parse(p.ElementAt(1));
                            vp1 = int.Parse(p.ElementAt(2));
                            vc1 = int.Parse(p.ElementAt(3));
                            a2 = Address.Parse(p.ElementAt(4));
                            vp2 = int.Parse(p.ElementAt(5));
                            vc2 = int.Parse(p.ElementAt(6));
                            int p1 = AddressToPort(a1); 
                            parent.removeSingleEntry(p1, vp1, vc1);
                            ZwolnijZasob(a1, a2);
                            nowakomenda = "MSG zadanie DELETE wykonane";
                        }
                        else
                        {
                            nowakomenda = "ERROR ZŁA LICZBA PARAMETRÓW W DELETE";
                        }
                        
                    }
                    else if (komenda.Equals("REQ_TOPOLOGY"))
                    {
                        nowakomenda = "TOPOLOGY";
                        for(int i=0;i<parent.routeList.Count;i++)
                        {
                            nowakomenda += " " + parent.routeList.ElementAt(i).destAddr.ToString();
                        }
                    }
                    else if(komenda.Equals("DEAD"))
                    {
                        bool czySonsiad = false;
                        Address sprawdzany = Address.Parse(pakiet.getParames().ElementAt(1));
                        for (int i = 0; i < parent.routeList.Count; i++ )
                        {
                            if(sprawdzany.Equals(parent.routeList.ElementAt(i).destAddr))
                            {
                                czySonsiad = true;
                                break;
                            }
                        }
                        if(czySonsiad)
                        {
                            SPacket wysylanyCC, wysylanyRC;
                            wysylanyCC = new SPacket(adresLRM, adresCC, "DEAD " + sprawdzany.ToString());
                            wysylanyRC = new SPacket(adresLRM, adresRC, "DEAD " + sprawdzany.ToString());
                            wyslijSPacket(wysylanyCC);
                            wyslijSPacket(wysylanyRC);
                        }
                        else
                        {
                            //do nothing?
                        }
                        continue;//nie swapujemy pakietu, bo to nie odpowiedź
                    }
                    else if(komenda.Equals("REQ_VPATHS"))
                    {
                        List<String> lista = new List<string>();
                        lista.Add("RES_VPATHS");
                        for(int i=0; i<parent.routeList.Count;i++)
                        {
                            String result = parent.routeList.ElementAt(i).destAddr.ToString();
                            for (int j = 0; j < parent.routeList.ElementAt(i).VPIList.Count; j++)
                            {
                                result += "#" + parent.routeList.ElementAt(i).VPIList.ElementAt(j).ToString();
                            }
                            lista.Add(result);
                        }
                        SPacket pkt = new SPacket(adresLRM, adresCC, lista);
                        wyslijSPacket(pkt);
                        continue;
                    }
                    pakiet.Swap(nowakomenda);//metoda zamienia src i dest i ustawia nowe parames
                    wyslijSPacket(pakiet);
                }
                catch (Exception e)
                {
                    Console.Out.WriteLine(e.Message);
                }
            }
        }
        #endregion
        #region dodaj pakiet ATM
        public void OdczytajATM(Packet.ATMPacket pkt)//każdy pakiet, od razu rozpoznaje rodzaj - czy zapytanie czy odpowiedź, innych nie powinno być, najwyzej przepadną, dodawanie do dobrej kolejki
        {
            String tresc = this.FromPayload(pkt.payload);
            if (tresc.Equals("ZYJESZ?"))
            {
                kolejkapyt.Enqueue(pkt);
            }
            else if (tresc.Equals("ZYJE"))
            {
                kolejkaodp.Add(pkt);
            }
           
        }
        #endregion
        #region sprawdzanie połączenia między Nodixami
        public void CzyZyjeRun(Tuple<Address, int> argus)//dla każdego zapytania o sprawność łącza od RC odpalany nowy wątek tą metodą
        {
            Thread ss =new Thread(new ParameterizedThreadStart(_CzyZyje));
            ss.IsBackground = true;
            ss.Start(argus);
        }


        private void _CzyZyje(Object argg)//pod dany adres (jaqk istnieje) wysyłamy ATMPacket z wiadomością ZYJESZ
        {
            Tuple<Address, int> argus = (Tuple<Address, int>)argg;
            bool wolne = false;
            int port=0;
            Address sprawdzany = argus.Item1;
            int i2 = argus.Item2;

            //szukanie portu dla adresu
            for (int i = 0; i < parent.routeList.Count; i++ )
            {
                if(parent.routeList.ElementAt(i).destAddr.Equals(sprawdzany))//jak adres się zgadza to mamy szukany port do wysyłki
                {
                    port = parent.routeList.ElementAt(i).port;
                    break;
                }
            }
            for (int i = 0; i < parent.routeList.Count; i++)
            {
                if (sprawdzany.Equals(parent.routeList.ElementAt(i).destAddr))//szukamy na routelist zadanego adresu
                {
                    if (parent.routeList.ElementAt(i).bandwidth >= 2)//gdy przepustowość co najmniej 2 to ok
                    {
                        wolne = true;
                        break;
                    }
                    else
                    {
                        wolne = false;
                        break;
                    }
                }
            }

            if(port==0)
            {
                //Wyslij z automatu no, bo nie ma takiego portu, żeby pakiet doszedł na ten adres
                return;
            }
            //wysyłka pakietu na wyszukanym porcie
            {
                String str = "ZYJESZ?";/////odpowiedzią na taki paylaod będzie ZYJE :)
                byte[] payload = ToPayload(str);
                Packet.ATMPacket packiet = new Packet.ATMPacket(Packet.ATMPacket.AALType.SSM, payload, 0, 0);
                packiet.port = port;
                packiet.VCI = -1;
                packiet.VPI = -1;
                parent.queuedReceivedPackets.Enqueue(packiet);
            }

            //czekanie na odp 
            {
                bool znaleziono=false;
                Stopwatch t = new Stopwatch();
                int maxwaitmilis = 3000; //czekaj do 3 sekund na odpowiedź od sąsiada
                t.Start();
                while (t.ElapsedMilliseconds < maxwaitmilis)
                {
                    //przeszukanie listy pakietów
                    for(int i=0;i<kolejkaodp.Count;i++)
                    {
                        if(kolejkaodp.ElementAt(i).port==port)
                        {
                            znaleziono = true;
                            kolejkaodp.RemoveAt(i);//usuwam bez dalszego zaglądania, bo jak coś siedzi w tej kolejce to ma w payload ZYJE
                            break;//gdy znaleziono to wychodzi z pętli for
                        }

                    }
                    if(znaleziono)
                    {
                        break;//gdy znaleziono to wychodzi z pętli while
                    }
                    Thread.Sleep(100);
                }
                //obsługa rezultatu, jeśli znaleziono to wyślij YES <adres>, jak nie to NO <adres>
                if(znaleziono && wolne)
                {
                    wyslijSPacket(new SPacket(adresLRM, adresRC, ("YES " + sprawdzany.ToString() + " " + i2)));
                }
                else
                {
                    wyslijSPacket(new SPacket(adresLRM, adresRC, ("NO " + sprawdzany.ToString() + " " + i2)));
                }

            }
        }
        #endregion
        #region TransferSPacket
        public void wyslijSPacket(Packet.SPacket cos)//wysyłanie przez chmurę kablową
        {
            parent.whatToSendQueue.Enqueue(cos);
        }
        public void OdczytajS(Packet.SPacket cos)//odbieranie z chmury kablowej
        {
            kolejkaS.Enqueue(cos);
        }
        #endregion
        #region konwersja payload<->String
        public byte[] ToPayload(String str)
        {
            return AAL.GetBytesFromString(str);
        }
        public String FromPayload(byte[] payload)
        {
            return AAL.GetStringFromBytes(payload);
        }
        #endregion
        #region zasoby
        public bool ZwolnijZasob(Address a1, Address a2)
        {
            int result=0;
            for (int i = 0; i < parent.routeList.Count; i++)
            {
                if (parent.routeList.ElementAt(i).Equals(a1) || parent.routeList.ElementAt(i).Equals(a2))//przeszukuje całą listę w poszukiwaniu zadanych adresów
                {
                    parent.routeList.ElementAt(i).bandwidth += 2;//zwalnianie zasobów
                    result++;
                }
                
            }
            if (result == 2)//gdy oba uda się zwolnić to sukces
                return true;
            else
                return false;
        }
        public bool ZajmijZasob(Address a1, Address a2)
        {
            int result = 0;
            for (int i = 0; i < parent.routeList.Count; i++)
            {
                if (parent.routeList.ElementAt(i).Equals(a1) || parent.routeList.ElementAt(i).Equals(a2))//przeszukuję całą listę
                {
                    if (parent.routeList.ElementAt(i).bandwidth<2)
                    {
                        break;
                    }
                    parent.routeList.ElementAt(i).bandwidth -= 2;//zajmuje zasób
                    result++;
                }

            }
            if (result == 2)//gdy oba uda się zająć to sukces
                return true;
            else
                return false;
        }
        #endregion
        #region konwersja Address<=>port
        public int AddressToPort(String S)//konwersja adresu zapisanego w stringu na przypisany mu port w routelist
        {
            Address spr = Address.Parse(S);
            return AddressToPort(spr);
        }
        public Address PortToAddress(int port)///zamiana portu na przypisany mu adres
        {
            Address result = null;
            for(int i=0; i<parent.routeList.Count;i++)
            {
                if(parent.routeList.ElementAt(i).port == port)
                {
                    result = parent.routeList.ElementAt(i).destAddr;
                }
            }
            return result;
        }
        public int AddressToPort(Address sprawdzany)//konwersja adresu na przypisany mu port w routelist
        {
            int port = 0;
            for (int i = 0; i < parent.routeList.Count; i++)
            {
                if (parent.routeList.ElementAt(i).destAddr.Equals(sprawdzany))//jak adres się zgadza to mamy szukany port do wysyłki
                {
                    port = parent.routeList.ElementAt(i).port;
                    break;
                }
            }
            return port;
        }
        #endregion
    }
}
