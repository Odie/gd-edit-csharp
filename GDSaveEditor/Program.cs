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

    public class StaticCount : System.Attribute
    {
        public readonly int count;
        public StaticCount(int count)
        {
            this.count = count;
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
            readVerifyBlockStartMarker(0, s, encrypter);
            
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
            readVerifyBlockEndMarker(0, s, encrypter);
            
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

        public class UID
        {
            public byte[] data = new byte[16];
        }

        private static Block3 ReadBlock3(Stream s, Encrypter encrypter)
        {
            // Check the ID of the block
            readVerifyBlockStartMarker(3, s, encrypter);
            
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
            readVerifyBlockEndMarker(3, s, encrypter);

            return data;
        }

        class SpawnPoints
        {
            public List<UID> spawnPointIDs = new List<UID>();
        }

        class Block5
        {
            public UInt32 version;

            [StaticCount(3)]
            public List<SpawnPoints> spawnPoints = new List<SpawnPoints>();

            [StaticCount(3)]
            public List<UID> currentRespawn = new List<UID>();
        }

        class Teleporters
        {
            public List<UID> teleporterIDs = new List<UID>();
        }

        class Block6
        {
            public UInt32 version;

            [StaticCount(3)]
            public List<Teleporters> teleporterPoints = new List<Teleporters>();
        }

        class Markers
        {
            public List<UID> markerIDs = new List<UID>();
        }

        class Block7
        {
            public UInt32 version;

            [StaticCount(3)]
            public List<Markers> markers = new List<Markers>();
        }

        public class CharacterSkill
        {
            public string SkillName;
            public UInt32 Level;
            public Boolean Enabled;
            public UInt32 DevotionLevel;
            public UInt32 DevotionExperience;
            public UInt32 SubLevel;
            public Boolean SkillActive;
            public Boolean SkillTransition;
            public string AutoCastSkillName;
            public string AutoCastControllerName;
        }

        private class ItemSkill
        {
            public string SkillName;
            public string AutoCastSkillName;
            public string AutoCastControllerName;
            public byte[] UnknownBytes = new byte[4];
            public string Unknown;
        }

        class Block8
        {
            public UInt32 version;
            public List<CharacterSkill> skills = new List<CharacterSkill>();
            public UInt32 masteriesAllowed;
            public UInt32 skillPointsReclaimed;
            public UInt32 devotionPointsReclaimed;
            public List<ItemSkill> itemSkills = new List<ItemSkill>();
        }

        class Tokens
        {
            public List<String> names = new List<String>();
        }

        class Block10
        {
            public UInt32 version;

            [StaticCount(3)]
            public List<Tokens> tokenPerDifficulty = new List<Tokens>();
        }

        class Shrines
        {
            public List<UID> shrineIDs = new List<UID>();
        }

        class Block17
        {
            public UInt32 version;

            [StaticCount(6)]
            public List<Shrines> shrines = new List<Shrines>();
        }

        class Block12
        {
            public UInt32 version;
            public List<string> loreItemNames = new List<string>();
        }

        private class Faction
        {
            public bool FactionChanged;
            public bool FactionUnlocked;
            public float FactionValue;
            public float PositiveBoost;
            public float NegativeBoost;
        }

        class Block13
        {
            public UInt32 version;
            public UInt32 myFaction;
            public List<Faction> factionValues = new List<Faction>();
        }

        class HotSlot
        {
            public string SkillName;
            public string ItemName;
            public string BitmapUp;
            public string BitmapDown;
            public string DefaultText;
            public uint HotSlotType;
            public uint ItemEquipLocation;
            public bool IsItemSkill;

            public static HotSlot Read(Stream s, Encrypter encrypter)
            {
                HotSlot hotSlot = new HotSlot();
                hotSlot.HotSlotType = Read_UInt32(s, encrypter);
                switch (hotSlot.HotSlotType)
                {
                    case 0:
                        hotSlot.SkillName = Read_String(s, encrypter);
                        hotSlot.IsItemSkill = Read_Bool(s, encrypter);
                        hotSlot.ItemName = Read_String(s, encrypter);
                        hotSlot.ItemEquipLocation = Read_UInt32(s, encrypter);
                        break;
                    case 4:
                        hotSlot.ItemName = Read_String(s, encrypter);
                        hotSlot.BitmapUp = Read_String(s, encrypter);
                        hotSlot.BitmapDown = Read_String(s, encrypter);
                        hotSlot.DefaultText = Read_WString(s, encrypter);
                        break;
                }
                return hotSlot;
            }
        }
        
        class Block14
        {
            public UInt32 version;
            public Boolean equipmentSelection;
            public UInt32 skillWindowSelection;
            public Boolean skillSettingValid;

            public string primarySkill1;
            public string secondarySkill1;
            public Boolean skillActive1;

            public string primarySkill2;
            public string secondarySkill2;
            public Boolean skillActive2;

            public string primarySkill3;
            public string secondarySkill3;
            public Boolean skillActive3;

            public string primarySkill4;
            public string secondarySkill4;
            public Boolean skillActive4;

            public string primarySkill5;
            public string secondarySkill5;
            public Boolean skillActive5;

            [StaticCount(36)]
            public List<HotSlot> hotSlots = new List<HotSlot>();

            public float cameraDistance;
        }

        class Block15
        {
            public UInt32 version;
            public List<UInt32> tutorialsUnlocked = new List<UInt32>();
        }

        public class GreatestMonsterKilled
        {
            public string name;
            public UInt32 level;
            public UInt32 lifeMana;
            public string lastMonsterHit;
            public string lastMonsterHitBy;
        }

        class Block16
        {
            public UInt32 version;
            public UInt32 playtimeSeconds;
            public UInt32 deathCount;
            public UInt32 killCount;
            public UInt32 experienceFromKills;
            public UInt32 healthPotionsUsed;
            public UInt32 energyPotionsUsed;
            public UInt32 maxLevel;
            public UInt32 hitReceived;
            public UInt32 hitsInflicted;
            public UInt32 critsInflicted;
            public UInt32 critsReceived;
            public float greatestDamageDone;

            [StaticCount(3)]
            public List<GreatestMonsterKilled> greatestMonsterKilled = new List<GreatestMonsterKilled>();

            public UInt32 championKills;
            public float lastMonsterHitDA;
            public float lastMonsterHitOA;
            public float greatestDamageReceived;
            public UInt32 heroKills;
            public UInt32 itemsCrafted;
            public UInt32 relicsCrafted;
            public UInt32 tier2RelicsCrafted;
            public UInt32 tier3RelicsCrafted;
            public UInt32 devotionShrinesUnlocked;
            public UInt32 oneShotChestsUnlocked;
            public UInt32 loreNotesCollected;

            [StaticCount(3)]
            public List<UInt32> bossKills = new List<UInt32>();

            // These 4 fields should only be parsed if version is 9
            public UInt32 survivalGreatestWave;
            public UInt32 survivalGreatestScore;
            public UInt32 survivalDefenseBuilt;
            public UInt32 survivalPowerUpsActivated;

            public UInt32 uniqueItemsFound;
            public UInt32 randomizedItemsFound;
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

        static bool isBasicType(Type type)
        {
            HashSet<Type> set = new HashSet<Type>() {
                typeof(string),
                typeof(bool),
                typeof(uint),
                typeof(byte),
                typeof(float),
            };

            if (set.Contains(type))
                return true;
            else
                return false;
        }

        // Given a "basic type", read and return such an object
        //
        // This is useful in the readStructure function where it tries to deal with
        // types in a more general way. Since we're not attaching these read functions
        // to the types/classes themselves, we cannot just directly invoke something.
        // We need a separate way to dispatch these call.
        //
        // It's not pretty, but it'll work for now.
        static object Read_Basic_Type(Type type, Stream s, Encrypter encrypter)
        {
            if (type == typeof(string))
                return Read_String(s, encrypter);
            else if (type == typeof(bool))
                return Read_Bool(s, encrypter);
            else if (type == typeof(uint))
                return Read_UInt32(s, encrypter);
            else if (type == typeof(byte))
                return Read_Byte(s, encrypter);
            else if (type == typeof(float))
                return Read_Float(s, encrypter);

            return null;
        }

        // Reads in a structure of the specified type from the stream.
        //
        // This function deserializes data in the order the fields are listed in the given type.
        // This means that the declared order is very important. If the declared order is wrong,
        // then the deserialization will be done incorrectly.
        // The benefit of such an approach is that we can encode the format of the character file
        // using the structure to deserialize into. We don't need to hand code the deserialization
        // of every field.
        static Object readStructure(Type type, Stream s, Encrypter encrypter) {
            // Create an instance of the object to be filled with data
            Object instance = Activator.CreateInstance(type);
            Console.WriteLine("Serializing {0}", type);

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
                else if (field.FieldType == typeof(byte[]))
                {
                    // Looks like we're expecting a byte array
                    byte[] array = (byte[])field.GetValue(instance);

                    // Read in the exact size we're expecting
                    array = Read_Bytes(s, encrypter, array.Length);

                    // Place the newly read data back into the field location
                    field.SetValue(instance, array);
                }
                else if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    // What kind of items do we want to read?
                    Type itemType = field.FieldType.GetGenericArguments()[0];

                    // How many of them are there?
                    int itemCount = 0;
                    StaticCount count = (StaticCount)field.GetCustomAttribute(typeof(StaticCount));
                    if (count != null)
                        itemCount = count.count;
                    else
                        itemCount = (int)Read_UInt32(s, encrypter);

                    Console.WriteLine("Substructure: {0}, count = {1}", itemType, itemCount);

                    // Where are we storing the items?
                    dynamic list = field.GetValue(instance);

                    // How will we read the item?
                    // We read in basic types differently than "complex" or "compound" types
                    bool basicType = isBasicType(itemType);

                    // Start reading
                    for(int i = 0; i < itemCount; i++)
                    {
                        // Read in a single item
                        dynamic item;

                        // If it's a basic type, try to read it
                        if (basicType)
                            item = Read_Basic_Type(itemType, s, encrypter);

                        // If the thing to be create has an attached static "Read" method, invoke it
                        else if (itemType.GetMethod("Read") != null && itemType.GetMethod("Read").IsStatic)
                        {
                            MethodInfo info = itemType.GetMethod("Read");
                            item = info.Invoke(null, new object[] { s, encrypter });
                        }

                        // Otherwise, maybe we can recurively create the structure
                        else
                            item = readStructure(itemType, s, encrypter);
                        list.Add(item);
                    }
                }
                else 
                    throw new Exception("I don't know how to handle this type of field!");

            }

            return instance;
        }

        // Merges the top level fields in the given object into the given dictionary.
        //
        // The declared block structures are used to deal with the file format. But since character related
        // data is scattered throughout so many structures, it is hard to locate the exact fields we want to
        // examine.
        //
        // This function is meant to merge the fields into a giant dictionary for better programmatic manipuation.
        // It's possible, though unlikely, that different blocks have identical field names. If such a case were
        // to occur, the most straight forward thing to do is to rename conflicting block fields.
        static bool mergeStructureIntoDictionary(Dictionary<string, object> character, object block)
        {
            var fieldInfos = block.GetType().GetFields();

            // Walk through all fields in the block
            foreach (var field in fieldInfos)
            {
                // Every block has a version field
                // They are part of the file format info, not character information
                // Skip those
                if (field.Name == "version")
                    continue;

                // Does the field alrady exist in the dictionary?
                // If so, we've come across a block fieldname conflict.
                // This needs to be resolved manually.
                if(character.ContainsKey(field.Name))
                    throw new Exception(String.Format("Duplicate key name {0} found when merging block fields", field.Name));

                // Otherwise, put the field into the character
                character[field.Name] = field.GetValue(block);
            }

            return true;
        }

        //**************************************************************************************
        // Block Reading utilities
        //
        static bool readVerifyBlockStartMarker(uint expectedBlockID, Stream s, Encrypter encrypter)
        {
            // Read out the block begin marker
            // This should indicate the type of the block to be read
            uint readBlockID = Read_UInt32(s, encrypter);
            if (readBlockID != expectedBlockID)
                throw new Exception(String.Format("Expected block {0} but got {1}!", expectedBlockID, readBlockID));

            return true;
        }

        // Read the length of the block
        // For some reason, the file format is such that reading this field requires *not* updating the encrypter state
        static uint readBlockLength(Stream s, Encrypter encrypter)
        {
            // Read the length of the block
            BinaryReader reader = new BinaryReader(s);
            return reader.ReadUInt32() ^ encrypter.state;
        }

        static bool readVerifyBlockEndMarker(uint expectedBlockID, Stream s, Encrypter encrypter)
        {
            // Read the block end marker
            BinaryReader reader = new BinaryReader(s);
            uint endMarker = reader.ReadUInt32();
            if (endMarker != encrypter.state)
                throw new Exception(String.Format("Wrong checksum at the end of block {0}!", expectedBlockID));

            return true;
        }

        // Reads a single block from the character file
        static Object readBlock(uint expectedBlockID, Type blockType, Stream s, Encrypter encrypter)
        {
            // Stream sync check
            // Verify that we're reading the beginning of a block
            // Will succeed or throw error
            readVerifyBlockStartMarker(expectedBlockID, s, encrypter);

            // Read the length of the block
            uint blockLength = readBlockLength(s, encrypter);
            long expectedBlockEndPosition = s.Position + (long)blockLength;

            // Read the content of the block
            object blockInstance = readStructure(blockType, s, encrypter);

            // Did we end on the right position?
            if (s.Position != expectedBlockEndPosition)
                throw new Exception(String.Format("Block {0} ended on the wrong position", expectedBlockID));

            // Stream sync check
            // Verify that we're reading the end of a block
            // Will succeed or throw error
            readVerifyBlockEndMarker(expectedBlockID, s, encrypter);
            
            return blockInstance;
        }


        static Dictionary<string, object> loadCharacterFile(string filepath)
        {
            List<object> blockList = new List<object>();

            // Read through all blocks and collect them into the block list
            using (FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read))
            {
                if (fs == null)
                    return null;

                fs.Position = 0L;
                BinaryReader reader = new BinaryReader(fs);

                // File Format notes:
                //  Encryption seed - 4 bytes
                //  Magic number - 4 bytes ("GDCX")
                //  Header version - 4 bytes (must be 1)

                // Read and seed the encrytpion table
                Encrypter enc = new Encrypter(reader.ReadUInt32() ^ 1431655765U);

                // Try to read the file marker ("GDCX")
                if (Read_UInt32(fs, enc) != 0x58434447)
                    throw new Exception("Incorrect magic ID read!");

                uint headerVersion = Read_UInt32(fs, enc);
                if (headerVersion != 1)
                    throw new Exception(String.Format("Incorrect header version!  Unknown version {0}", headerVersion));

                Header header = (Header)readStructure(typeof(Header), fs, enc);
                blockList.Add(header);

                uint checksum = reader.ReadUInt32();
                if (checksum != enc.state)
                    throw new Exception("Checksum mismatch!");

                uint dataVersion = Read_UInt32(fs, enc);
                if (dataVersion != 6 && dataVersion != 7)
                    throw new Exception(String.Format("Incorrect data version!  Unknown version {0}.", dataVersion));

                byte[] mysteryField = Read_Bytes(fs, enc, 16);


                // TODO!!! It might be a good idea to peak at the file to determine type of the next block to be read
                // instead of relying on a specific order.
                Block1 block1 = (Block1)readBlock(1, typeof(Block1), fs, enc);
                blockList.Add(block1);
                Block2 block2 = (Block2)readBlock(2, typeof(Block2), fs, enc);
                blockList.Add(block2);
                Block3 block3 = ReadBlock3(fs, enc);
                blockList.Add(block3);
                Block4 block4 = (Block4)readBlock(4, typeof(Block4), fs, enc);
                blockList.Add(block4);
                Block5 block5 = (Block5)readBlock(5, typeof(Block5), fs, enc);
                blockList.Add(block5);
                Block6 block6 = (Block6)readBlock(6, typeof(Block6), fs, enc);
                blockList.Add(block6);
                Block7 block7 = (Block7)readBlock(7, typeof(Block7), fs, enc);
                blockList.Add(block7);
                Block17 block17 = (Block17)readBlock(17, typeof(Block17), fs, enc);
                blockList.Add(block17);
                Block8 block8 = (Block8)readBlock(8, typeof(Block8), fs, enc);
                blockList.Add(block8);
                Block12 block12 = (Block12)readBlock(12, typeof(Block12), fs, enc);
                blockList.Add(block12);
                Block13 block13 = (Block13)readBlock(13, typeof(Block13), fs, enc);
                blockList.Add(block13);
                Block14 block14 = (Block14)readBlock(14, typeof(Block14), fs, enc);
                blockList.Add(block14);
                Block15 block15 = (Block15)readBlock(15, typeof(Block15), fs, enc);
                blockList.Add(block15);
                Block16 block16 = (Block16)readBlock(16, typeof(Block16), fs, enc);
                blockList.Add(block16);
                Block10 block10 = (Block10)readBlock(10, typeof(Block10), fs, enc);
                blockList.Add(block10);

                // Did we read through the entire file?
                if (fs.Position != fs.Length)
                    throw new Exception("Done reading character file but did not reach EOF");
            }

            // Merge all blocks to get an overview of all character properties
            Dictionary<string, object> character = new Dictionary<string, object>();
            foreach(var block in blockList)
            {
                mergeStructureIntoDictionary(character, block);
            }

            // Hold on to the block list.
            // We're likely to need this when we serialize the character out to a file again.
            character["blockList"] = blockList;

            return character;
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
