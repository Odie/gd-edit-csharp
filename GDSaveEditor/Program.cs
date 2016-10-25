using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace GDSaveEditor
{
    public class OnDiskEncoding : System.Attribute
    {
        public readonly System.Type encoding;
        public OnDiskEncoding(System.Type encoding)
        {
            this.encoding = encoding;
        }
    }

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

        private static byte[] Read_Bytes(Stream s, Encrypter encrypter, int byteCount)
        {
            // FIXME!!! Allocate small buffers over and over again is typically very "slow".
            // But since we're processing such small save files, we'll just abuse the GC a bit.
            byte[] data = new byte[byteCount];
            s.Read(data, 0, byteCount);

            for (int i = 0; i < data.Length; i++)
            {
                byte decryptedVal = (byte)(data[i] ^ (byte)encrypter.state);
                encrypter.updateState(data[i]);
                data[i] = decryptedVal;
            }

            return data;
        }

        private static byte Read_Byte(Stream s, Encrypter encrypter)
        {
            return Read_Bytes(s, encrypter, 1)[0];
        }

        private static bool Read_Bool(Stream s, Encrypter encrypter)
        {
            return Read_Byte(s, encrypter) == 1;
        }

        //private int Read_Int32()
        //{
        //    byte[] numArray = new byte[4];
        //    this.msStream.Read(numArray, 0, 4);
        //    int num = checked((int)((long)BitConverter.ToInt32(numArray, 0) ^ (long)this.checksum));
        //    this.Hash(numArray, 4U);
        //    return num;
        //}

        // Read a 4 byte value
        // Note that we cannot use the "Read_bytes" function because they make use of the encrypter state differently.
        // This function uses the entire 4 bytes of the encrypter state to decrypt the value.
        // Read_Bytes will ignore the 3 higher bytes.
        private static uint Read_UInt32(Stream s, Encrypter encrypter)
        {   
            byte[] data = new byte[4];
            s.Read(data, 0, 4);
            uint val = BitConverter.ToUInt32(data, 0) ^ encrypter.state;
            encrypter.updateState(data);
            return val;
        }

        private static float Read_Float(Stream s, Encrypter encrypter)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(Read_UInt32(s, encrypter)), 0);
        }

        private static string Read_String(Stream s, Encrypter encrypter)
        {
            uint length = Read_UInt32(s, encrypter);
            if (length == 0)
                return string.Empty;
            if (length >= int.MaxValue)
                throw new Exception("Too many bytes to read!");

            byte[] data = Read_Bytes(s, encrypter, (int)length);
            return Encoding.ASCII.GetString(data);
        }

        private static string Read_WString(Stream s, Encrypter encrypter)
        {
            uint length = Read_UInt32(s, encrypter);
            if (length == 0)
                return string.Empty;
            if (length >= int.MaxValue)
                throw new Exception("Too many bytes to read!");

            byte[] data = Read_Bytes(s, encrypter, (int)length * 2);
            return Encoding.Unicode.GetString(data);
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
                    updateState(data[i]);
                }
            }

            public void updateState(byte data)
            {
                state = state ^ encTable[data];
            }

            public static uint[] generateTable(uint seed)
            {
                uint[] table = new uint[byte.MaxValue+1]; // 256 entry table
                uint val = seed;
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
                        val = BitConverter.ToUInt32(
                                BitConverter.GetBytes(
                                    Convert.ToInt64(val << 31 | val >> 1) *
                                    Convert.ToInt64(39916801))
                                , 0);

                        table[i] = val;
                    }
                }

                return table;
            }
        }

        class Header
        {
            [OnDiskEncoding(typeof(UnicodeEncoding))]
            public string characterName;

            public Boolean male;
            public string playerClassName;
            public UInt32 characterLevel;
            public bool hardcoreMode;
        }

        class Block1
        {
            public UInt32 version;
            public Boolean inMainQuest;
            public Boolean hasBeenInGame;
            public Byte lastDifficulty;
            public Byte greatestDifficultyCompleted;
            public UInt32 ironMode;
            public Byte greatestSurvivalDifficultyCompleted;
            public UInt32 tributes;
            public Byte UICompassState;
            public UInt32 alwaysShowLootMode;
            public Boolean showSkillHelp;
            public Boolean altWeaponSet;
            public Boolean altWeaponSetEnabled;
            public string playerTexture;
        }

        class Block2
        {
            public UInt32 version;
            public UInt32 levelInBio;
            public UInt32 experience;
            public UInt32 modifierPoints;
            public UInt32 skillPoints;
            public UInt32 devotionPoints;
            public UInt32 totalDevotionPointsUnlocked;
            public float physique;
            public float cunning;
            public float spirit;
            public float health;
            public float energy;
        }

        public abstract class Item
        {
            public string baseName;
            public string prefixName;
            public string suffixName;
            public string modifierName;
            public string transmuteName;
            public uint seed;

            public string relicName;
            public string relicBonus;
            public uint relicSeed;

            public string augmentName;
            public uint unknown;
            public uint augmentSeed;

            public uint var1;
            public uint stackCount;
        }

        public class InventoryItem : Item
        {
            public uint X;
            public uint Y;
        }

        public class StashItem : Item
        {
            public float X;
            public float Y;
        }

        public class EquipmentItem : Item
        {
            public bool Attached;
        }

        public class InventorySack
        {
            public bool UnusedBoolean;
            public List<InventoryItem> items = new List<InventoryItem>();
        }

        private static InventorySack ReadSack(Stream s, Encrypter encrypter)
        {
            // Check the ID of the block
            uint expectedBlockID = 0;
            uint readBlockID = Read_UInt32(s, encrypter);
            if (readBlockID != expectedBlockID)
                throw new Exception(String.Format("Expected block {0} but got {1}!", expectedBlockID, readBlockID));

            // Read the length of the block
            BinaryReader reader = new BinaryReader(s);
            uint blockLength = reader.ReadUInt32() ^ encrypter.state;

            // Initialize an equipment sack
            InventorySack sack = new InventorySack();
            sack.UnusedBoolean = Read_Bool(s, encrypter);
            int numItems = (int)Read_UInt32(s, encrypter);

            // Read in the items in the sack
            for (int i = 0; i < numItems; i++)
                sack.items.Add((InventoryItem)readStructure(typeof(InventoryItem), s, encrypter));

            // Read the block end marker
            uint endMarker = reader.ReadUInt32();
            if (endMarker != encrypter.state)
                throw new Exception(String.Format("Wrong checksum at the end of block {0}!", expectedBlockID));

            return sack;
        }

        class Block3
        {
            public UInt32 version;
            public UInt32 sackCount;
            public UInt32 focusedSack;
            public UInt32 selectedSack;
            public List<InventorySack> inventorySacks = new List<InventorySack>();
            public Boolean useAltWeaponSet;
            public List<EquipmentItem> equipment = new List<EquipmentItem>();
            public Boolean alternate1;
            public List<EquipmentItem> alternateSet1 = new List<EquipmentItem>();
            public Boolean alternate2;
            public List<EquipmentItem> alternateSet2 = new List<EquipmentItem>();
        }

        class Block4
        {
            public UInt32 version;
            public UInt32 stashWidth;
            public UInt32 stashHeight;
            //public UInt32 numStashItems;
            public List<StashItem> stashItems = new List<StashItem>();
        }

        private static Block3 ReadBlock3(Stream s, Encrypter encrypter)
        {
            // Check the ID of the block
            uint expectedBlockID = 3;
            uint readBlockID = Read_UInt32(s, encrypter);
            if (readBlockID != expectedBlockID)
                throw new Exception(String.Format("Expected block {0} but got {1}!", expectedBlockID, readBlockID));

            // Read the length of the block
            BinaryReader reader = new BinaryReader(s);
            uint blockLength = reader.ReadUInt32() ^ encrypter.state;

            Block3 data = new Block3();

            data.version = Read_UInt32(s, encrypter);
            if(data.version != 4)
                throw new Exception(String.Format("Inventory block version mismatch!  Unknown version {0}.", data.version));

            bool hasData = Read_Bool(s, encrypter);
            if(hasData)
            {
                data.sackCount = Read_UInt32(s, encrypter);
                data.focusedSack = Read_UInt32(s, encrypter);
                data.selectedSack = Read_UInt32(s, encrypter);

                // Read all sacks
                for (int i = 0; i < data.sackCount; i++)
                    data.inventorySacks.Add(ReadSack(s, encrypter));

                data.useAltWeaponSet = Read_Bool(s, encrypter);

                // Read equipment
                for (int i = 0; i < 12; i++)
                {
                    data.equipment.Add((EquipmentItem)readStructure(typeof(EquipmentItem), s, encrypter));
                }
  
                // Read alternate set 1
                data.alternate1 = Read_Bool(s, encrypter);
                for (int i = 0; i < 2; i++)
                {
                    data.alternateSet1.Add((EquipmentItem)readStructure(typeof(EquipmentItem), s, encrypter));
                }

                // Read alternate set 2
                data.alternate2 = Read_Bool(s, encrypter);
                for (int i = 0; i < 2; i++)
                {
                    data.alternateSet2.Add((EquipmentItem)readStructure(typeof(EquipmentItem), s, encrypter));
                }
            }

            // Read the block end marker
            uint endMarker = reader.ReadUInt32();
            if (endMarker != encrypter.state)
                throw new Exception(String.Format("Wrong checksum at the end of block {0}!", expectedBlockID));

            return data;
        }

        // Builds a flattened "ordered" list of field names given a type.
        //
        // For some reason, when you ask for a list of fields for a class, the fields come in "reverse hiearchy order".
        // As in, the fields in the leaf nodes are stated first, then the fields in the parent/base classes.
        // This means we need to reshuffle the fields a bit to get them into the "correct" order.
        //
        // In this case, "correct" is the order we want to deserialize the data.
        // This problem was encountered when trying to deserialize various different types of items.
        // There are apparently 3 different types/formats for an item, and they different slightly in the fields that
        // they carry. We want to abuse the hierarchy tree so we don't need to duplicate identical fields and still
        // get the correct serialization order.
        static List<FieldInfo> buildOrderedFieldList(Type type)
        {
            var hierarchyOrder = new List<Type>();

            Type typeCursor = type;
            while (typeCursor != typeof(Object))
            {
                hierarchyOrder.Add(typeCursor);
                typeCursor = typeCursor.BaseType;
            }

            hierarchyOrder.Reverse();

            var orderedFieldList = new List<FieldInfo>();
            foreach(var t in hierarchyOrder)
            {
                var fields = t.GetFields();
                var wantedFields = fields.Take(fields.Length - orderedFieldList.Count);
                orderedFieldList.AddRange(wantedFields);
            }
            return orderedFieldList;
        }

        static Object readStructure(Type type, Stream s, Encrypter encrypter) {
            // Create an instance of the object to be filled with data
            Object instance = Activator.CreateInstance(type);

            var fieldInfos = buildOrderedFieldList(type);
            foreach (var field in fieldInfos)
            {
                if(field.FieldType == typeof(string))
                {
                    // Determine the encoding we want to use to read the data
                    // Default to reading everything as ascii
                    Type encoding = typeof(ASCIIEncoding);

                    // Some items should be read as UTF-16. 
                    // Those fields will be have a custom attribute on them to override the default.
                    OnDiskEncoding spec = (OnDiskEncoding)field.GetCustomAttribute(typeof(OnDiskEncoding));
                    if (spec != null)
                        encoding = spec.encoding;

                    // Read the string
                    // NOTE: We're using the .NET encoding types as some sort of enum to get the compiler to
                    // help us not enter gibberish as the encoding.
                    string value = null;
                    if (encoding == typeof(ASCIIEncoding))
                        value = Read_String(s, encrypter);
                    else if (encoding == typeof(UnicodeEncoding))
                        value = Read_WString(s, encrypter);
                    else
                        throw new Exception("Bad structure declaration!");

                    field.SetValue(instance, value);
                }
                else if (field.FieldType == typeof(bool))
                {
                    field.SetValue(instance, Read_Bool(s, encrypter));
                }
                else if (field.FieldType == typeof(uint))
                {
                    field.SetValue(instance, Read_UInt32(s, encrypter));
                }
                else if (field.FieldType == typeof(byte))
                {
                    field.SetValue(instance, Read_Byte(s, encrypter));
                }
                else if (field.FieldType == typeof(float))
                {
                    field.SetValue(instance, Read_Float(s, encrypter));
                }
                else if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    // What kind of items do we want to read?
                    Type itemType = field.FieldType.GetGenericArguments()[0];

                    // How many of them are there?
                    UInt32 itemCount = Read_UInt32(s, encrypter);

                    // Where are we storing the items?
                    dynamic list = field.GetValue(instance);

                    // Start reading
                    for(int i = 0; i < itemCount; i++)
                    {
                        // Read in a single item
                        dynamic item = readStructure(itemType, s, encrypter);
                        list.Add(item);
                    }
                }
                else 
                    throw new Exception("I don't know how to handle this type of field!");

            }

            return instance;
        }

        // Reads a single block from the character file
        static Object readBlock(uint expectedBlockID, Type blockType, Stream s, Encrypter encrypter)
        {
            // Read out the block begin marker
            // This should indicate the type of the block to be read
            uint readBlockID = Read_UInt32(s, encrypter);
            if (readBlockID != expectedBlockID)
                throw new Exception(String.Format("Expected block {0} but got {1}!", expectedBlockID, readBlockID));

            // Read the length of the block
            BinaryReader reader = new BinaryReader(s);
            uint blockLength = reader.ReadUInt32() ^ encrypter.state;

            // Read the content of the block
            object blockInstance = readStructure(blockType, s, encrypter);

            // Read the block end marker
            uint endMarker = reader.ReadUInt32();
            if (endMarker != encrypter.state)
                throw new Exception(String.Format("Wrong checksum at the end of block {0}!", expectedBlockID));

            return blockInstance;
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
            //  Header version - 4 bytes (must be 1)

            // Read and seed the encrytpion table
            Encrypter enc = new Encrypter(reader.ReadUInt32() ^ 1431655765U);

            // Try to read the file marker ("GDCX")
            if(Read_UInt32(fs, enc) != 0x58434447)
                throw new Exception("Incorrect magic ID read!");

            uint headerVersion = Read_UInt32(fs, enc);
            if(headerVersion != 1)
                throw new Exception(String.Format("Incorrect header version!  Unknown version {0}", headerVersion));

            Header header = (Header)readStructure(typeof(Header), fs, enc);

            uint checksum = reader.ReadUInt32();
            if(checksum != enc.state)
                throw new Exception("Checksum mismatch!");

            uint dataVersion = Read_UInt32(fs, enc);
            if(dataVersion != 6 && dataVersion != 7)
                throw new Exception(String.Format("Incorrect data version!  Unknown version {0}.", dataVersion));

            byte[] mysteryField = Read_Bytes(fs, enc, 16);

            Block1 block1 = (Block1)readBlock(1, typeof(Block1), fs, enc);
            Block2 block2 = (Block2)readBlock(2, typeof(Block2), fs, enc);
            Block3 block3 = ReadBlock3(fs, enc);
            Block4 block4 = (Block4)readBlock(4, typeof(Block4), fs, enc);
            return;
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
