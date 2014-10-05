using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Nodix {
    static class Program {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args) {
            String nNumber;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Nodix n = new Nodix();
            if (args != null) {
                try {
                    nNumber = args[0];
                    n.readConfig(nNumber);
                    n.connect = true;
                } catch {}
            }
            Application.Run(n);
        }
    }
}