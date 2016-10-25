using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GDSaveEditor
{
    public static class Extensions
    {
        public static void Each<T>(this IEnumerable<T> ie, Action<T, int> action)
        {
            var i = 0;
            foreach (var e in ie) action(e, i++);
        }
    }

    class ActionItem
    {
        public ActionItem(string selectorIn, string textIn, Action actionIn)
        {
            selector = selectorIn;
            text = textIn;
            action = actionIn;
        }

        public string selector;
        public string text;
        public Action action;
    }

    class Globals
    {
        public static List<ActionItem> activeActionMap;
        public static string activeCharacterFile;
    }

    class Program
    {

        static void saveFileSelectionScreen()
        {
            Console.WriteLine("The following characters were found. Please select one to edit.");

            // Find all the save files!
            var saveFileDirs = findSaveFileDirs();

            // Build an action map with all character directory entries
            var actionMap = new List<ActionItem>();

            saveFileDirs.Each((saveFileDir, idx) =>
            {
                actionMap.Add(new ActionItem(
                    (idx+1).ToString(), 
                    saveFileDir.Substring(saveFileDir.LastIndexOf("_")), 
                    () => {
                        Globals.activeCharacterFile = saveFileDir;
                        characterManipuationScreen();
                    }));
            });

            Globals.activeActionMap = actionMap;
        }

        static void characterManipuationScreen()
        {
            Console.WriteLine("File: {0}", Globals.activeCharacterFile);

            var actionMap = new List<ActionItem>
            {
                new ActionItem("r", "Reload", () => { loadCharacterFile(Path.Combine(Globals.activeCharacterFile, "player.gdc")); })
            };

            Globals.activeActionMap = actionMap;
        }

        // Print whatever is in the action map
        static void printActiveActionMap()
        {  
            foreach (var action in Globals.activeActionMap)
            {
                Console.WriteLine("{0}) {1}", action.selector, action.text);
            }
        }

        static void processInput(string input)
        {
            // Try to look up the input in the action map
            foreach(var action in Globals.activeActionMap)
            {
                // If we found an action with a matching selector...
                if(input == action.selector && action.action != null)
                {
                    // Run the action now
                    action.action();
                    return;
                }
            }

            // Otherwise, try to deal with it as an application input
            if (input.ToLower() == "exit")
                Environment.Exit(0);

            // If we can't deal with it, print an error
            Console.WriteLine("Couldn't make heads or tails out of that one. Try again?");
            Console.WriteLine();
        }

        static void Main(string[] args)
        {
            saveFileSelectionScreen();

            while(true)
            {
                printActiveActionMap();
                Console.WriteLine();
                Console.Write("> ");

                var input = Console.ReadLine();
                Console.WriteLine();
                processInput(input);
            }
            
        }

        private static byte Read_Byte(Stream s, Encrypter encrypter)
        {
            // FIXME!!! Allocate small buffers over and over again is typically very "slow".
            // But since we're processing such small save files, we'll just abuse the GC a bit.
            byte[] data = new byte[1];
            s.Read(data, 0, 1);
            uint val = (uint)data[0] ^ encrypter.state;
            encrypter.updateState(data);
            return BitConverter.GetBytes(val)[0];
        }

        private static bool Read_Bool(Stream s, Encrypter encrypter)
        {
            return Read_Byte(s, encrypter) == 1;
        }

        private static uint Read_UInt32(Stream s, Encrypter encrypter)
        {
            byte[] data = new byte[4];
            s.Read(data, 0, 4);
            uint val = BitConverter.ToUInt32(data, 0) ^ encrypter.state;
            encrypter.updateState(data);
            return val;
        }

        class Encrypter
        {
            uint[] encTable;
            public uint state;

            public Encrypter(uint seed)
            {
                encTable = generateTable(seed);
                state = seed;
            }

            public void updateState(byte[] data)
            {
                for(int i = 0; i < data.Length; i++)
                {
                    state = state ^ encTable[data[i]];
                }
            }

            public static uint[] generateTable(uint seed)
            {
                uint[] table = new uint[byte.MaxValue]; // 256 entry table
                uint num1 = seed;
                uint idx = 0;
                checked
                {
                    // Construct a table of 256 value for decrytpion and encryption.
                    // We start with some state value = seed
                    // With each byte read, update the state value like this:
                    //  state = state ^ table[ byte_value ]
                    // The value of latter bytes are effected by value of previous bytes.
                    // This also means if we fail to update our state with any byte read, 
                    // we'll get junk data out. 
                    for (uint i = 0; i <= byte.MaxValue; i++)
                    {
                        num1 = BitConverter.ToUInt32(
                                BitConverter.GetBytes(
                                    Convert.ToInt64(num1 << 31 | num1 >> 1) *
                                    Convert.ToInt64(39916801))
                                , 0);

                        table[idx] = num1;
                    }
                }
                return table;
            }
        }
        

        static void loadCharacterFile(string filepath)
        {
            FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read);
            if (fs == null)
                return;

            fs.Position = 0L;
            BinaryReader reader = new BinaryReader(fs);

            // File Format notes:
            //  Encryption seed - 4 bytes
            //  Magic number - 4 bytes ("GDCX")

            // Read and seed the encrytpion table
            Encrypter enc = new Encrypter(reader.ReadUInt32() ^ 1431655765U);

            // Try to read the file marker ("GDCX")
            if(Read_UInt32(fs, enc) != 0x58434447)
                throw new Exception("Incorrect magic ID read!");

        }

        // Get back a list of grim dawn save directories that appears to have a player.gdc file
        static List<string> findSaveFileDirs()
        {
            var saveFiles = new List<string>();

            // Locate the Steam user data directory
            string progdir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string cloudSavePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam\\userdata\\");
            if (!Directory.Exists(cloudSavePath))
                return saveFiles;

            // Iterate through all found account directories
            List<string> accountDirs = new List<string>(Directory.EnumerateDirectories(cloudSavePath));
            foreach (var dir in accountDirs)
            {
                // Ignore account directories without the grim dawn remote save directory
                string saveFileDir = Path.Combine(dir, "219990\\remote\\save\\main");
                if (!Directory.Exists(saveFileDir))
                    continue;

                List<string> characterDirs = new List<string>(Directory.EnumerateDirectories(saveFileDir));
                foreach (var characterDir in characterDirs)
                {
                    if (!File.Exists(Path.Combine(characterDir, "player.gdc")))
                        continue;

                    saveFiles.Add(characterDir);
                }
            }
            return saveFiles;
        }
    }
}
