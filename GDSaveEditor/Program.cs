using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Console = Colorful.Console;
using System.Drawing;

using DBResult = System.Collections.Generic.IEnumerable<
                        System.Collections.Generic.KeyValuePair<
                            string, 
                            System.Collections.Generic.Dictionary<
                                string, 
                                object>>>;

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

    delegate void ShowScreenState();

    class QueryHistory
    {
        public List<string> queryParams;
        public DBResult collection;
    }

    class Globals
    {
        public static ShowScreenState showScreenState;
        public static List<ActionItem> activeActionMap;
        public static string activeCharacterFile;
        public static Dictionary<string, object> character;
        public static Dictionary<string, Dictionary<string, object>> db;
        public static Dictionary<string, string> tags;

        public static List<QueryHistory> queryHistory = new List<QueryHistory>();
        public static int recordsToSkip;
    }

    class Program
    {
        static Dictionary<string, string> readTagFile(Stream s)
        {
            var reader = new StreamReader(s);
            var dict = new Dictionary<string, string>();

            while (!reader.EndOfStream)
            {
                string line = reader.ReadLine();

                // Look for a "=" as the key and value separator
                // There are empty lines and comments in the tag files also.
                // In general, it looks like we can ignore the line if we can't find a "=" symbol
                int sepIndex = line.IndexOf('=');
                if (sepIndex == -1) {
                    continue;
                }

                string key = line.Substring(0, sepIndex);
                string val = line.Substring(sepIndex+1, line.Length - sepIndex - 1);
                dict[key] = val;
            }

            return dict;
        }

        static Dictionary<string, string> readAllTags(string filepath)
        {
            // Unpack all the localization/tag files
            var contents = ArcReader.read(filepath);

            // Put all the tags into a single dictionary
            var tags = new Dictionary<string, string>();
            foreach(var item in contents)
            {
                if(!item.Key.StartsWith("tag"))
                    continue;

                var dict = readTagFile(new MemoryStream(item.Value));

                foreach(var pair in dict)
                {
                    // We're not expecting any duplicate keys/tags
                    Debug.Assert(!tags.ContainsKey(pair.Key));

                    // Add the new tags into the dictionary
                    tags[pair.Key] = pair.Value;
                }
            }
            return tags;
        }

        static string getNextBackupFilepath(string path)
        {
            var filename = Path.GetFileName(path);
            string[] files = Directory.GetFiles(Path.GetDirectoryName(path), filename+".bak*");

            string backupFilename;
            if (files.Length == 0)
                backupFilename = filename + ".bak";
            else
                backupFilename =  filename + ".bak" + files.Length;

            return Path.Combine(Path.GetDirectoryName(path), backupFilename);
        }

        static string getCharacterFilepath(string characterDir)
        {
            return Path.Combine(Globals.activeCharacterFile, "player.gdc");
        }

        static void saveFileSelectionScreen()
        {
            Globals.showScreenState = new ShowScreenState(() =>
            {
                Console.WriteLine("The following characters were found. Please select one to edit.");
            });

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
            Globals.showScreenState = new ShowScreenState(() =>
            {
                Console.WriteLine("File: {0}", Globals.activeCharacterFile);
            });

            // If we haven't loaded the character yet, auto load it now
            if (Globals.character == null && Globals.activeCharacterFile != null)
                Globals.character = loadCharacterFile(getCharacterFilepath(Globals.activeCharacterFile));

            var actionMap = new List<ActionItem>
            {
                new ActionItem("c", "Character selection", () => {
                    saveFileSelectionScreen();
                }),
                new ActionItem("r", "Reload", () => {
                    Globals.character = loadCharacterFile(getCharacterFilepath(Globals.activeCharacterFile));
                }),
                new ActionItem("w", "Write", () => {
                    // Make a backup of the current file
                    var characterFilepath = getCharacterFilepath(Globals.activeCharacterFile);
                    var backupFilepath = getNextBackupFilepath(characterFilepath);
                    File.Move(characterFilepath, backupFilepath);

                    // Write out the new character file
                    mergeCharacterIntoBlockList(Globals.character);
                    writeCharacterFile(characterFilepath, Globals.character);
                }),
            };


            Globals.activeActionMap = actionMap;
        }

        static void mergeCharacterIntoBlockList(Dictionary<string, object> character)
        {
            List<object> blockList = (List<object>)character["meta-blockList"];
            foreach(var pair in character)
            {
                if (pair.Key.StartsWith("meta-"))
                    continue;

                // Find a block with the matching field name
                // We should get a match of exactly one block
                var matches = blockList.Where(b => b.GetType().GetField(pair.Key) != null);
                if (matches.Count() != 1)
                    throw new Exception(String.Format("Error merging field: {0}", pair.Key));
                var block = matches.First();

                // Set the value into the field
                block.GetType().GetField(pair.Key).SetValue(block, pair.Value);
            } 
        }

        // Print whatever is in the action map
        static void printActiveActionMap()
        {  
            foreach (var action in Globals.activeActionMap)
            {
                Console.WriteLine("{0}) {1}", action.selector, action.text);
            }
        }

        static void printDictionary(IEnumerable<KeyValuePair<string, object>> collectionIn, int indentLevel = 0)
        {
            var collection = collectionIn
                                .Where(p => !p.Key.StartsWith("meta-"))
                                .OrderBy(pair => pair.Key);

            if (collection.Count() == 0)
            {
                Console.WriteLine("No matches", Color.Red);
                return;
            }

            foreach (KeyValuePair<string, object> entry in collection)
            {
                printTabs(indentLevel);
                Console.Write("{0}: ", entry.Key, Color.White);

                Type valueType = entry.Value.GetType();
                if (valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    dynamic list = entry.Value;
                    Console.WriteLine("{0} item(s) of type: {1}", list.Count, valueType.GetGenericArguments()[0].Name, Color.DarkGray);
                }
                else
                    Console.WriteLine(entry.Value);
            }

            Console.WriteLine();
            printTabs(indentLevel);
            Console.Write(collection.Count(), Color.White);
            Console.WriteLine(" item(s) shown", Color.DarkGray);
        }

        static bool verifyCharacterLoaded(Dictionary<string, object> character)
        {
            if (character != null)
                return true;

            Console.WriteLine("No character has been loaded yet\nThere is nothing to show");
            return false;
        }

        static string[] splitCamelCase(string input)
        {
            var words = Regex.Matches(input, "(^[a-z]+|[A-Z]+(?![a-z])|[A-Z][a-z]+)")
                                .OfType<Match>()
                                .Select(m => m.Value)
                                .ToArray();
            return words;
        }

        static void printTabs(int indentLevel)
        {
            for (int i = 0; i < indentLevel; i++)
                Console.Write("\t");
        }

        static void printObject(object o, int indentLevel = 0)
        {
            Type type = o.GetType();

            // If we have a basic type, just print it.
            if (isBasicType(type))
            {
                printTabs(indentLevel);
                Console.Write(o);
                return;
            }

            // If we have a list, then print the contents of the list
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                dynamic list = o;
                for(int i = 0; i < list.Count; i++)
                {
                    var item = list[i];
                    Console.WriteLine("{0}: ", i);
                    printObject(item, indentLevel);
                }
                return;
            }

            if (type.IsGenericType && 
                (type.GetGenericTypeDefinition() == typeof(Dictionary<,>) || type.GetGenericTypeDefinition().Namespace == "System.Linq"))
            {
                printDictionary((IEnumerable<KeyValuePair<string, object>>)o);
                return;
            }

            // When we're here, we should be looking at some kind of "complex" type.
            Debug.Assert(!type.IsGenericType);
            printStructure(o, indentLevel);
        }

        static void printStructure(object structure, int indentLevel = 0)
        {
            var fieldInfos = buildOrderedFieldList(structure.GetType());
            foreach (var field in fieldInfos)
            {
                for (int i = 0; i < indentLevel; i++)
                    Console.Write("\t");
                Console.WriteLine("{0}: {1}", field.Name, field.GetValue(structure));
            }
        }

        static IEnumerable<KeyValuePair<string, object>> exactFieldMatch(Dictionary<string, object> dictionary, string target)
        {
            var collection = dictionary
                                .Where(p => !p.Key.StartsWith("meta-"))
                                .Where(pair =>
                                    pair.Key == target);

            return collection;
        }

        static IEnumerable<KeyValuePair<string, object>> partialFieldMatch(Dictionary<string, object> dictionary, string target)
        {
            var collection = dictionary
                                .Where(p => !p.Key.StartsWith("meta-"))
                                .Where(pair =>
                                    pair.Key.ToLower().Contains(target));

            return collection;
        }

        static bool isDictionaryWithStringKeys(object obj)
        {
            Type type = obj.GetType();
            if (type.IsGenericType && 
                type.GetGenericTypeDefinition() == typeof(Dictionary<,>) && 
                type.GetGenericArguments()[0] == typeof(string))
                return true;

            return false;
        }

        // Sets a field on the given object regardless of wether it's a dictionary of some other complex type
        static void setField(object obj, string fieldname, object val)
        {
            Type type = obj.GetType();

            // Are we looking at a dictionary?
            if (isDictionaryWithStringKeys(obj))
            {
                dynamic dictionary = obj;
                dictionary[fieldname] = val;
                return;
            }

            // We're not going to deal with any other types of generic containers
            Debug.Assert(!type.IsGenericType);

            // Grab the field info and set the field to the new value
            FieldInfo fieldInfo = type.GetField(fieldname);
            if (fieldInfo == null)
                throw new MissingFieldException();

            fieldInfo.SetValue(obj, val);
        }

        static bool showCommandHandler(List<string> parameters)
        {
            if (!verifyCharacterLoaded(Globals.character))
                return true;

            if (parameters.Count == 0)
            {
                // When there are no parameters, simply show the entire character map
                var collection = Globals.character;
                printDictionary(collection);
                return true;
            }

            if (parameters.Count == 1)
            {
                var sep = "/".ToCharArray();
                var path = parameters[0].TrimEnd(sep).Split(sep);

                object target = Globals.character;
                Dictionary<string, object> walkResult;

                // If the path more than a single component, try walking the structure
                // with partial fieldname matching.
                // However, since we're traversing a field, we cannot allow any ambiguity in the path.
                // We leave out the last component to allow for multiple matches.
                // This way, "show" can be used to explore, filter, and navigate.
                if (path.Length > 1)
                {
                    walkResult = walkStructure(Globals.character, path.Take(path.Length-1).ToList());
                    if ((bool)walkResult["walkCompleted?"] == false)
                    {
                        Console.WriteLine("No match found", Color.DarkRed);
                        return true;
                    }
                    target = walkResult["target"];
                }

                // Grab the last component of the path
                // We're going to allow for multiple matches here.
                walkResult = walkStructure(target, path.Skip(path.Length - 1).ToList());
                if ((bool)walkResult["walkCompleted?"] == false)
                {
                    if (walkResult["terminationReason"] == "ambiguous")
                    {
                        printObject(walkResult["ambiguousTarget"], 1);
                    }
                    else
                    {
                        Console.WriteLine("No match found", Color.DarkRed);
                        return true;
                    }
                    return true;
                }

                target = walkResult["target"];
                if (walkResult["targetFieldname"] != null)
                {
                    Console.Write("{0}: ", walkResult["targetFieldname"]);
                    if (!isBasicType(target.GetType()))
                        Console.WriteLine();
                }
                printObject(target, 1);

                return true;
            }

            Console.Write("Syntax: show <partial fieldname>");
            return false;
        }

        static bool setCommandHandler(List<string> parameters)
        {
            if (!verifyCharacterLoaded(Globals.character))
                return true;

            if (parameters.Count == 2)
            {
                var sep = "/".ToCharArray();
                var path = parameters[0].TrimEnd(sep).Split(sep);

                // If the path more than a single component, try walking the structure
                // with partial fieldname matching.
                // However, since we're traversing a field, we cannot allow any ambiguity in the path.
                // We leave out the last component to allow for multiple matches.
                // This way, "show" can be used to explore, filter, and navigate.
                Dictionary<string, object> walkResult;
                walkResult = walkStructure(Globals.character, path.ToList());
                if ((bool)walkResult["walkCompleted?"] == false)
                {
                    if ((string)walkResult["terminationReason"] == "ambiguous")
                        Console.Write("The path specifies more than one item");
                    else
                        Console.Write("No match found for: {0}", parameters[0]);
                    return true;
                }

                object targetParent = walkResult["targetParent"];
                string fieldname = (string)walkResult["targetFieldname"];
                object target = walkResult["target"];

                // If we're here, the match count should be exactly 1.
                // So we can try to set the field to something now.
                // First, check if we can coerce the new value to the correct type of the field.

                // FIXME!!! Getting the target parent field type this way only deals with complex types
                // If we ever have a dictionary in the middle of the data hiearchy somewhere, this will break!
                Type fieldType = null;
                if (isDictionaryWithStringKeys(targetParent))
                    fieldType = ((Dictionary<string, object>)targetParent)[fieldname].GetType();
                else
                    fieldType = targetParent.GetType().GetField(fieldname).FieldType;

                // Now that we know what the field type is, we can try to coerce the user input into the correct type
                dynamic val;
                if (fieldType == typeof(uint))
                    val = Convert.ToUInt32(parameters[1]);
                else if (fieldType == typeof(float))
                    val = Convert.ToSingle(parameters[1]);
                else if (fieldType == typeof(bool))
                    val = Convert.ToBoolean(parameters[1]);
                else if (fieldType == typeof(byte))
                    val = Convert.ToByte(parameters[1]);
                else
                {
                    // If it's not any of the other types, just treat it as a string
                    val = parameters[1];
                    val = val.Trim("\"".ToCharArray());
                }

                setField(targetParent, fieldname, val);

                Console.WriteLine("Updated value:");
                processCommand("show " + parameters[0]);
                return true;
            }

            Console.Write("Syntax: set <fieldname> <new value>");
            return true;
        }

        private static void dbResultResetPagination()
        {
            Globals.recordsToSkip = 0;
        }

        private static bool dbHistoryBackHandler(List<string> parameters)
        {
            List<QueryHistory> list = Globals.queryHistory;
            if (list.Count == 0)
                return true;

            list.RemoveAt(list.Count() - 1);
            dbResultResetPagination();
            dbShowHistoryHandler(new List<string>());
            return true;
        }

        private static bool dbShowHistoryHandler(List<string> parameters)
        {
            List<QueryHistory> list = Globals.queryHistory;
            if(list.Count() == 0)
            {
                Console.WriteLine("Query history is empty");
                return true;
            }

            for(int i = 0; i < list.Count(); i++)
            {
                var history = list[i];
                Console.WriteLine("{0}:\t{1}", i + 1, string.Join(" ", history.queryParams.ToArray()));
            }
            return true;
        }

        private static bool dbShowResultHandler(List<string> parameters)
        {
            // Want to start over from showing record #0?
            if(parameters.Count() > 0 && parameters[0] == "restart")
            {
                dbResultResetPagination();
            }

            if(Globals.queryHistory.Count() == 0)
            {
                Console.WriteLine("There are no results to show yet");
                return true;
            }
            QueryHistory history = Globals.queryHistory.Last();

            var results = history.collection;
            var recordCount = results.Count();
            var fieldCount = results.Sum(record => record.Value.Count());

            // Print each of the records in the result we retrieved...
            var recordsShown = 0;
            var fieldsShown = 0;

            // Did we finish displaying all the records?
            // Start over again at the first record
            if(Globals.recordsToSkip >= recordCount)
            {
                dbResultResetPagination();
            }
            int displayStart = Globals.recordsToSkip;
            int recordsToSkip = Globals.recordsToSkip;
            foreach(var item in history.collection)
            {
                if(recordsToSkip != 0)
                {
                    recordsToSkip--;
                    continue;
                }
                Console.WriteLine("{0}:", item.Key);
                printDictionary(item.Value, 1);
                Console.WriteLine();

                // Limit ourselves to a sane number of records
                // There can be a LOT of them
                recordsShown++;
                if (recordsShown >= 100)
                    break;

                fieldsShown += item.Value.Count();
                if (fieldsShown >= 300)
                    break;
            }

            Globals.recordsToSkip += recordsShown;
            Console.WriteLine();
            Console.WriteLine("{0}-{1}/{2} records shown", displayStart, Globals.recordsToSkip,results.Count());
            return true;
        }

        private static bool queryCompare(object lhs, object rhs, string op)
        {
            var lhsType = lhs.GetType();
            if(op == "=")
            {
                return lhs.Equals(rhs);
            }
            else if(op == "!=")
            {
                return !lhs.Equals(rhs);
            }
            if (lhsType == typeof(uint))
            {
                if(op == ">")
                {
                    return (uint)lhs > (int)rhs;  
                }
                else if(op == "<")
                {
                    return (uint)lhs < (int)rhs;  
                }
                if (op == ">=")
                {
                    return (uint)lhs >= (int)rhs;
                }
                else if (op == "<=")
                {
                    return (uint)lhs <= (int)rhs;
                }
                return false;
            }
            else if(lhsType == typeof(string))
            {
                if (op == "~")
                {
                    return ((string)lhs).ToLower().Contains(rhs.ToString());
                }
                return false;
            }

            return false;
        }

        static bool dbQueryVerifyTarget(string target)
        {
            if (target != "recordname" && target != "key" && target != "value") {
                Console.WriteLine("'{0} is not a valid target. Valid options are: 'recordname', 'key', or 'value'", target);
                return false;
            }
            return true;
        }

        static bool dbQueryVerifyOp(string op)
        {
            if(op != "~" && op != "=" && op != ">" && op != "<" && op != "!=" && op != ">="&& op != "<=")
            {
                Console.WriteLine("Valid ops are: ~, =, >, <, !=, >=, <=");
                return false;
            }
            return true;
        }

        static object dqQueryCoerceValue(string valueString)
        {
            if (valueString == null)
                return 0;

            // Try to coerce the input into whatever type it looks like
            // Integers will take precedence over floats
            var intRegex = new Regex(@"^[0-9]*$");
            var floatRegex = new Regex(@"^[0-9]*(?:\.[0-9]*)?$");

            object value = null;

            if (intRegex.IsMatch(valueString)) {
                int v;
                var parseResult = int.TryParse(valueString, out v);
                value = v;
                Debug.Assert(parseResult);
            }
            else if (floatRegex.IsMatch(valueString))
            {
                float v;
                var parseResult = float.TryParse(valueString, out v);
                value = v;
                Debug.Assert(parseResult);
            }
            else
                value = valueString;

            return value;
        }

        // Since we have to work with two separate set of conditions now...
        // We need to figure out how to "bind" the various variables into the linq
        // query. If we can order the two clauses into a definite order and simplify
        // the rest of the code.
        //
        // Returns:
        //   0 - equal precedence
        //  <0 - target 1 is of higher precedence
        //  >0 - target 2 is of higher precedence
        static int dqQueryTargetPrecedence(string target1, string target2)
        {
            int score1 = dqQueryTargetAssignPrecedenceScore(target1);
            int score2 = dqQueryTargetAssignPrecedenceScore(target2);
            return score1 - score2;
        }

        static int dqQueryTargetAssignPrecedenceScore(string target)
        {
            if(target == "recordname")
                return 1;
            if (target == "key")
                return 2;
            if (target == "value")
                return 3;

            throw new Exception("Bad target value sent for precedence");
            return 0;
        }

        static void swap<T>(ref T x, ref T y)
        {
            T t = y;
            y = x;
            x = t;
        }

        private static bool dbQueryHandler(List<string> parameters)
        {
            // The expected syntax is <target field> <operator> <value>
            // String matches are look like this:
            //  recordname ~ "suffix"
            //
            // valid target fields are:
            //  "recordname" => targets database record name
            //  "key" => targets recrod keyname
            //  "value" => targets record value
            //
            //  valid operators are:
            //   ~  => partial string match
            //   =  => exact match
            //   >  => greather than
            //   <  => less than
            //   != => not equal
            //
            //  valid values:
            //   string
            //   signed int
            //   float

            if (Globals.db == null)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Globals.db = ArzReader.read("C:\\Program Files (x86)\\Steam\\steamapps\\common\\Grim Dawn\\database\\database.arz");
                timer.Stop();
                Console.WriteLine("{0:##.##} seconds to read the db", timer.ElapsedMilliseconds/1000f);

                timer.Restart();
                Globals.tags = readAllTags("C:\\Program Files (x86)\\Steam\\steamapps\\common\\Grim Dawn\\resources\\text_en.arc");
                timer.Stop();
                Console.WriteLine("{0:##.##} seconds to read the tag files", timer.ElapsedMilliseconds/1000f);

                // We want to update all db fields where some string content starts with "tag" to the English display string
                // which is stored in the tags table now.

                // Iterate through all records
                timer.Restart();
                var db = Globals.db;
                var tags = Globals.tags;
                foreach(var recordname in db.Keys.ToList())
                {
                    var record = db[recordname];

                    // Iterate through all fields
                    foreach(var fieldname in record.Keys.ToList())
                    {
                        var fieldValue = record[fieldname];

                        // If the field is a string...
                        if (fieldValue.GetType() != typeof(string))
                            continue;

                        // And the field starts with "tag"
                        var tagName = (string)fieldValue;
                        if(tagName.StartsWith("tag"))
                        {
                            // Grab a the corresponding value in the tag table/
                            string tagValue = null;
                            if(Globals.tags.TryGetValue(tagName, out tagValue))
                            {
                                // Set the value into the correct location in the database
                                Globals.db[recordname][fieldname] = tagValue;
                            }

                        }
                    }
                }
                timer.Stop();
                Console.WriteLine("{0:##.##} seconds to update the db tagnames", timer.ElapsedMilliseconds/1000f);
            }

            // Reconstruct, then tokenize the input 
            string originalInput = string.Join(" ", parameters.ToArray());
            var matches = Regex.Matches(originalInput, @"[\""].+?[\""]|[^ ]+");
            parameters = matches.Cast<Match>().Select(match => match.Value.Trim(" \"".ToCharArray())).ToList();

            if (parameters.Count() == 1 && (parameters[0] == "restart" || parameters[0] == "new"))
            {
                Globals.queryHistory.Clear();
                Console.WriteLine("Okay! Ready to start new query!");
                return true;
            }

            if (parameters.Count() != 3 && parameters.Count() != 6)
            {
                Console.WriteLine("Syntax: q <target field> <op> <value>");
                return true;
            }

            var target = parameters[0].ToLower();
            string target2 = (parameters.Count() > 3 ? parameters[3] : null);
            if (!dbQueryVerifyTarget(target))
                return true;
            if(target2 != null)
                if (!dbQueryVerifyTarget(target))
                    return true;

            var op = parameters[1];
            var op2 = (parameters.Count() > 3 ? parameters[4] : null);
            if (!dbQueryVerifyOp(op))
                return true;
            if(op2 != null)
                if (!dbQueryVerifyOp(op2))
                    return true;

            // We have some value.
            // Try to coerce it into whatever type it looks like
            // Integers will take precedence over floats
            var valueString = parameters[2];
            var valueString2 = (parameters.Count() > 3 ? parameters[5] : null);
            var value = dqQueryCoerceValue(valueString);
            var value2 = dqQueryCoerceValue(valueString2);

            // Setup the query
            // Retreive the dataset we're basing our query from.
            // This could be the result of a previous query or the whole dataset
            DBResult result = null;
            DBResult lastResult = null;
            if (Globals.queryHistory.Count == 0)
                lastResult = Globals.db;
            else
                lastResult = Globals.queryHistory.Last().collection;


            // Bind & execute the query
            // Only have one clause?
            if (parameters.Count() == 3)
            {
                if (target == "recordname")
                {
                    result = lastResult.Where(record =>
                        queryCompare(record.Key, valueString, op));
                }

                // key comparison
                else if (target == "key")
                {
                    result = lastResult.Where(record =>
                        record.Value.Where(kv =>
                                queryCompare(kv.Key, valueString, op)).Any());
                }

                // value comparison
                else if (target == "value")
                {
                    result = lastResult.Where(record =>
                        record.Value.Where(kv =>
                                queryCompare(kv.Value, value, op)).Any());
                }
            }
            else
            {
                // Have two clauses to deal with?
                // To introduce some sanity, we try to order the two sets of variables
                // so it's easier to determine which variable should be bound to which in the linq
                // query.
                var precedence = dqQueryTargetPrecedence(target, target2);
                if (precedence == 0)
                {
                    Console.WriteLine("Can't deal with two clauses on the same target");
                    return true;
                }
                if(precedence > 0)
                {
                    swap(ref target, ref target2);
                    swap(ref op, ref op2);
                    swap(ref valueString, ref valueString2);               
                }
                    
                if(target == "recordname" && target2 == "key")
                {
                    result = lastResult.Where(record =>
                        queryCompare(record.Key, valueString, op) &&
                        record.Value.Where(kv =>
                                queryCompare(kv.Key, value2, op)).Any());
                }
                else if(target == "key" && target2 == "value")
                {
                    result = lastResult.Where(record =>
                        record.Value.Where(kv =>
                                queryCompare(kv.Key, value, op) &&
                                queryCompare(kv.Value, value2, op2)).Any());

                }
                else if(target == "recordname" && target2 == "value")
                {
                    result = lastResult.Where(record =>
                        queryCompare(record.Key, valueString, op) &&
                        record.Value.Where(kv =>
                                queryCompare(kv.Value, value2, op)).Any());
                }
            }

            var history = new QueryHistory();
            history.queryParams = parameters;
            history.collection = result;
            Globals.queryHistory.Add(history);
            dbResultResetPagination();

            // Print the results
            processCommand("qshow restart"); 
            return true;
        }

        // Returns whether the command was understood and handled.
        static bool processCommand(string input)
        {
            var tokens = input.Split(" ".ToCharArray());
            var command = tokens[0].ToLower();
            var parameters = tokens.Skip(1).ToList();

            if (command == "exit" || command == "quit")
            {
                Environment.Exit(0);
                return true;
            }

            if (command == "show")
                return showCommandHandler(parameters);

            else if (command == "set")
                return setCommandHandler(parameters);
            else if (command == "q")
                return dbQueryHandler(parameters);
            else if (command == "qshow" || command == "qs")
                return dbShowResultHandler(parameters);
            else if (command == "qpath")
                return dbShowHistoryHandler(parameters);
            else if (command == "qback")
                return dbHistoryBackHandler(parameters);

            return false;
        }


        // Given some data structure and a path, we try to traverse as much of the path as possible.
        static Dictionary<string, object> walkStructure(object structure, List<string> path)
        {
            dynamic targetParent = null;
            dynamic target = structure;
            string lastTargetFieldname = null;
            var result = new Dictionary<string, object>();

            int i;
            for(i = 0; i < path.Count; i++)
            {
                var pathItem = path[i];
                Type targetType = target.GetType();

                bool skipExactMatch = false;
                if(pathItem.Last() == '*')
                {
                    skipExactMatch = true;
                    pathItem = pathItem.TrimEnd("*".ToCharArray());
                }

                // If we're looking at a basic type, there is no way to further navigate into the data
                // heiarchy.
                if (isBasicType(targetType))
                    break;

                // If we're looking at a list, try to parse the path item as an index and navigate to the
                // specified item.
                if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    int index;
                    bool isNumeric = int.TryParse(pathItem, out index);
                    if (isNumeric && index < target.Count) {
                        targetParent = target;
                        target = target[index];
                        lastTargetFieldname = pathItem;
                        continue;
                    }
                    break;
                }

                // If we're looking at a dictionary...
                if (isDictionaryWithStringKeys(target))
                {
                    // Try to perform a partial match on the current path item
                    IEnumerable<KeyValuePair<string, object>> exactMatches = null;
                    if(!skipExactMatch)
                        exactMatches = exactFieldMatch(target, pathItem);
                    IEnumerable<KeyValuePair<string, object>> partialMatches = partialFieldMatch(target, pathItem);
                    var collection = (exactMatches != null && exactMatches.Count() == 1 ? exactMatches : partialMatches);

                    // Make sure we're only dealing with one result
                    // If we have more than one result, that means the given path specifies more than one item.
                    // It's ambiguous.
                    if (collection.Count() == 0)
                    {
                        result["terminationReason"] = "no match";
                        break;
                    }

                    if(collection.Count() != 1)
                    {
                        result["terminationReason"] = "ambiguous";
                        result["ambiguousTarget"] = collection;
                        break;
                    }

                    targetParent = target;
                    lastTargetFieldname = collection.First().Key;
                    target = collection.First().Value;
                    continue;
                }

                // We're looking at some other kinds of complex type
                // It's also possible that there is some other type of generics data structure along the path.
                // We don't want to deal with those ATM.
                Debug.Assert(!targetType.IsGenericType);
                {
                    // Can we find a field in the structure to navigate to?
                    // If not, we're done traversing the path
                    System.Collections.Generic.IEnumerable<FieldInfo> exactMatches = null;
                    if(!skipExactMatch)
                        exactMatches = targetType.GetFields().Where(fieldInfo =>
                                                            fieldInfo.Name == pathItem);
                    var partialMatches = targetType.GetFields().Where(fieldInfo =>
                                                            fieldInfo.Name.ToLower().Contains(pathItem));
                    var collection = (exactMatches != null && exactMatches.Count() == 1 ? exactMatches : partialMatches);

                    if (collection.Count() == 0)
                    {
                        result["terminationReason"] = "no match";
                        break;
                    }

                    if(collection.Count() != 1)
                    {
                        result["terminationReason"] = "ambiguous";
                        result["ambiguousTarget"] = collection;
                        break;
                    }

                    FieldInfo targetField = collection.First();
                    targetParent = target;
                    lastTargetFieldname = targetField.Name;
                    target = targetField.GetValue(target);
                }
            }

            result["walkCompleted?"] = (i == path.Count);
            result["pathTraversed"]= path.Take(i).ToList();
            result["pathRemaining"] = path.Skip(i).ToList();
            result["targetParent"] = targetParent;
            result["target"] = target;
            result["targetFieldname"] = lastTargetFieldname;

            return result;
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

            // Otherwise, try to deal with it as an application command
            if (processCommand(input))
                return;

            // If we can't deal with it, print an error
            Console.WriteLine("Couldn't make heads or tails out of that one. Try again?");
        }

        static void Main(string[] args)
        {
            saveFileSelectionScreen();

            while(true)
            {
                if (Globals.showScreenState != null)
                {
                    Globals.showScreenState();
                    Console.WriteLine();
                }
                printActiveActionMap();
                Console.WriteLine();
                Console.Write("> ", Color.Green);
                Console.ResetColor();

                var input = Console.ReadLine();
                Console.WriteLine();
                processInput(input);
                Console.WriteLine();
                Console.WriteLine("----------", Color.DarkGoldenrod);
                Console.ResetColor();
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

        private static void Write_Bytes(Stream s, Encrypter encrypter, byte[] dataIn)
        {
            // Make a copy of the data
            // We're going to be mutate/encrypt the entire array before writing it out
            byte[] data = new byte[dataIn.Length];
            dataIn.CopyTo(data, 0);

            if (encrypter != null)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    byte encryptedVal = (byte)(data[i] ^ (byte)encrypter.state);
                    encrypter.updateState(encryptedVal);
                    data[i] = encryptedVal;
                }
            }

            s.Write(data, 0, data.Length);
        }

        private static byte Read_Byte(Stream s, Encrypter encrypter)
        {
            return Read_Bytes(s, encrypter, 1)[0];
        }

        private static void Write_Byte(Stream s, Encrypter encrypter, byte data)
        {
            // FIXME!!! This seems extremely wasteful
            byte[] array = new byte[1] { data };
            Write_Bytes(s, encrypter, array);
        }

        private static bool Read_Bool(Stream s, Encrypter encrypter)
        {
            return Read_Byte(s, encrypter) == 1;
        }

        private static void Write_Bool(Stream s, Encrypter encrypter, bool data)
        {
            Write_Byte(s, encrypter, data ? (byte)1 : (byte)0);
        }

        // NOTE: We're not decrypting UInt16 values because they are not used by the save file format.
        internal static UInt16 Read_UInt16(Stream s, Encrypter encrypter)
        {   
            byte[] data = new byte[2];
            s.Read(data, 0, 2);
            return BitConverter.ToUInt16(data, 0);
        }

        internal static Int32 Read_Int32(Stream s, Encrypter encrypter)
        {   
            byte[] data = new byte[4];
            s.Read(data, 0, 4);
            return BitConverter.ToInt32(data, 0);
        }

        internal static UInt64 Read_UInt64(Stream s, Encrypter encrypter)
        {   
            byte[] data = new byte[8];
            s.Read(data, 0, 8);
            return BitConverter.ToUInt64(data, 0);
        }

        // Read a 4 byte value
        // Note that we cannot use the "Read_bytes" function because they make use of the encrypter state differently.
        // This function uses the entire 4 bytes of the encrypter state to decrypt the value.
        // Read_Bytes will ignore the 3 higher bytes.
        internal static uint Read_UInt32(Stream s, Encrypter encrypter)
        {   
            byte[] data = new byte[4];
            s.Read(data, 0, 4);
            uint val = BitConverter.ToUInt32(data, 0);
            if (encrypter != null) {
                val = val ^ encrypter.state;
                encrypter.updateState(data);
            }
            return val;
        }

        private static void Write_UInt32(Stream s, Encrypter encrypter, UInt32 data)
        {
            UInt32 val = data;

            // Encrypt the value is an encrypter is given
            if (encrypter != null) {
                val = val ^ encrypter.state;
                encrypter.updateState(BitConverter.GetBytes(val));
            }

            s.Write(BitConverter.GetBytes(val), 0, 4);
        }

        internal static float Read_Float(Stream s, Encrypter encrypter)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(Read_UInt32(s, encrypter)), 0);
        }

        private static void Write_Float(Stream s, Encrypter encrypter, float data)
        {
            // We want to interpret the float as a 4 byte quantity and write it via Write_UInt32.
            // Perhaps a better name for the function is "Write_4bytes".
            // This cannot be replaced with "Write_Bytes" because the encrypter state is used
            // differently. Write_Bytes uses a single byte out of the state and discards the rest 3 
            // bytes. Write_UInt32 uses the entire 4 bytes.
            Write_UInt32(s, encrypter, BitConverter.ToUInt32(BitConverter.GetBytes(data), 0));
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

        private static void Write_String(Stream s, Encrypter encrypter, string data)
        {
            Write_UInt32(s, encrypter, (uint)data.Length);
            if (data.Length == 0)
                return;

            Write_Bytes(s, encrypter, Encoding.ASCII.GetBytes(data));
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

        private static void Write_WString(Stream s, Encrypter encrypter, string data)
        {
            Write_UInt32(s, encrypter, (uint)data.Length);
            if (data.Length == 0)
                return;

            Write_Bytes(s, encrypter, Encoding.Unicode.GetBytes(data));
        }

        internal class Encrypter
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
            public Boolean hardcoreMode;
        }

        class Block1
        {
            public UInt32 version;
            public Boolean inMainQuest;
            public Boolean hasBeenInGame;
            public Byte lastDifficulty;
            public Byte greatestDifficultyCompleted;
            public UInt32 iron;
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

            static public InventorySack Read(Stream s, Encrypter encrypter)
            {
                // Check the ID of the block
                readVerifyBlockStartMarker(0, s, encrypter);
                
                // Read the length of the block
                uint blockLength = readBlockLength(s, encrypter);

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

            static public void Write(Stream s, Encrypter encrypter, InventorySack sack)
            {
                // We're ready to output the contents to file
                // Write the ID of the block to be written
                Write_UInt32(s, encrypter, 0);

                // Write the length of the block
                long blockStartPos = s.Position;
                uint blockStartEncState = encrypter.state;
                s.Write(BitConverter.GetBytes(0), 0, 4);

                // Since we are in the write method, and writeStructure will try to invoke it if possible,
                // make sure to tell it to skip the "write" method  
                writeStructure(sack, s, encrypter, WriteStructureOption.SkipWriteMethod);

                // Now that we've written the data, go back and fill in the length field
                writeBlockLength(s, encrypter, blockStartPos, s.Position, blockStartEncState);

                // Write the encrypter state for stream syncing
                s.Write(BitConverter.GetBytes(encrypter.state), 0, 4);
            }

            public void Write(Stream s, Encrypter encrypter)
            {
                Write(s, encrypter, this);
            }
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

            static public Block3 Read(Stream s, Encrypter encrypter)
            {
                Block3 data = new Block3();

                data.version = Read_UInt32(s, encrypter);
                if (data.version != 4)
                    throw new Exception(String.Format("Inventory block version mismatch!  Unknown version {0}.", data.version));

                bool hasData = Read_Bool(s, encrypter);
                if (hasData)
                {
                    data.sackCount = Read_UInt32(s, encrypter);
                    data.focusedSack = Read_UInt32(s, encrypter);
                    data.selectedSack = Read_UInt32(s, encrypter);

                    // Read all sacks
                    for (int i = 0; i < data.sackCount; i++)
                        data.inventorySacks.Add(InventorySack.Read(s, encrypter));

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

                return data;
            }

            static public void Write(Stream s, Encrypter encrypter, Block3 data)
            {
                // MiscNote: This isn't likely to ever happen. The game starts with the character sacks and some equipment!
                Write_UInt32(s, encrypter, data.version);
                if (data.sackCount == 0 && data.focusedSack == 0 && data.selectedSack == 0)
                {
                    Write_Bool(s, encrypter, false);
                    return;
                }

                Write_Bool(s, encrypter, true);
                Write_UInt32(s, encrypter, data.sackCount);
                Write_UInt32(s, encrypter, data.focusedSack);
                Write_UInt32(s, encrypter, data.selectedSack);

                // Write all sacks
                for (int i = 0; i < data.sackCount; i++)
                    InventorySack.Write(s, encrypter, data.inventorySacks[i]);

                Write_Bool(s, encrypter, data.useAltWeaponSet);

                // Write equipment
                for (int i = 0; i < 12; i++)
                    writeStructure(data.equipment[i], s, encrypter);

                // Write alternate set 1
                Write_Bool(s, encrypter, data.alternate1);
                for (int i = 0; i < 2; i++)
                    writeStructure(data.alternateSet1[i], s, encrypter);

                // Read alternate set 2
                Write_Bool(s, encrypter, data.alternate2);
                for (int i = 0; i < 2; i++)
                    writeStructure(data.alternateSet2[i], s, encrypter);
            }

            public void Write(Stream s, Encrypter encrypter)
            {
                Write(s, encrypter, this);
            }
        }

        class Block4
        {
            public UInt32 version;
            public UInt32 stashWidth;
            public UInt32 stashHeight;
            public List<StashItem> stashItems = new List<StashItem>();
        }

        public class UID
        {
            public byte[] data = new byte[16];
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

            public static void Write(Stream s, Encrypter encrypter, HotSlot data)
            {
                Write_UInt32(s, encrypter, data.HotSlotType);
                switch (data.HotSlotType)
                {
                    case 0:
                        Write_String(s, encrypter, data.SkillName);
                        Write_Bool(s, encrypter, data.IsItemSkill);
                        Write_String(s, encrypter, data.ItemName);
                        Write_UInt32(s, encrypter, data.ItemEquipLocation);
                        break;
                    case 4:
                        Write_String(s, encrypter, data.ItemName);
                        Write_String(s, encrypter, data.BitmapUp);
                        Write_String(s, encrypter, data.BitmapDown);
                        Write_WString(s, encrypter, data.DefaultText);
                        break;
                }
            }

            public void Write(Stream s, Encrypter encrypter)
            {
                Write(s, encrypter, this);
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
            if (type == typeof(string) ||
                type == typeof(bool) ||
                type == typeof(UInt32) ||
                type == typeof(UInt16) ||
                type == typeof(Int32) ||
                type == typeof(UInt64) ||
                type == typeof(byte) ||
                type == typeof(float))
                return true;

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
            else if (type == typeof(UInt32))
                return Read_UInt32(s, encrypter);
            else if (type == typeof(Int32))
                return Read_Int32(s, encrypter);
            else if (type == typeof(UInt16))
                return Read_UInt16(s, encrypter);
            else if (type == typeof(UInt64))
                return Read_UInt64(s, encrypter);
            else if (type == typeof(byte))
                return Read_Byte(s, encrypter);
            else if (type == typeof(float))
                return Read_Float(s, encrypter);

            return null;
        }

        static void Write_Basic_Type(object data, Stream s, Encrypter encrypter)
        {
            Type type = data.GetType();
            if (type == typeof(string))
                Write_String(s, encrypter, (string)data);
            else if (type == typeof(bool))
                Write_Bool(s, encrypter, (bool)data);
            else if (type == typeof(uint))
                Write_UInt32(s, encrypter, (UInt32)data);
            else if (type == typeof(byte))
                Write_Byte(s, encrypter, (byte)data);
            else if (type == typeof(float))
                Write_Float(s, encrypter, (float)data);
            else
                throw new Exception("Got a non-basic type!");
        }

        // Reads in a structure of the specified type from the stream.
        //
        // This function deserializes data in the order the fields are listed in the given type.
        // This means that the declared order is very important. If the declared order is wrong,
        // then the deserialization will be done incorrectly.
        // The benefit of such an approach is that we can encode the format of the character file
        // using the structure to deserialize into. We don't need to hand code the deserialization
        // of every field.
        internal static Object readStructure(Type type, Stream s, Encrypter encrypter) {
            if (isBasicType(type))
                return Read_Basic_Type(type, s, encrypter);

            // If the type has supplied a custom "Read" handler, call it.
            MethodInfo methodInfo = type.GetMethod("Read", BindingFlags.Static | BindingFlags.Public);
            if (methodInfo != null)
            {
                return methodInfo.Invoke(null, new object[] { s, encrypter });
            }

            // Neither of those methods worked.
            // We'll try to read in the structure driven by the fields of the structure itself

            // Create an instance of the object to be filled with data
            Object instance = Activator.CreateInstance(type);
            //Console.WriteLine("Deserializing {0}", type);

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
                else if (isBasicType(field.FieldType))
                {
                    field.SetValue(instance, Read_Basic_Type(field.FieldType, s, encrypter));
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

                    //Console.WriteLine("Substructure: {0}, count = {1}", itemType, itemCount);

                    // Where are we storing the items?
                    dynamic list = field.GetValue(instance);

                    // How will we read the item?
                    // We read in basic types differently than "complex" or "compound" types
                    bool basicType = isBasicType(itemType);

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

        enum WriteStructureOption{
            Normal = 0,
            SkipWriteMethod = 1,
        }
        
        static void writeStructure(object instance, Stream s, Encrypter encrypter, WriteStructureOption option = WriteStructureOption.Normal) {
            Type type = instance.GetType();
            //Console.WriteLine("Serializing {0}", type);

            if (isBasicType(type))
            {
                Write_Basic_Type(instance, s, encrypter);
                return;
            }

            // If the type has supplied a custom "Write" handler, call it.
            MethodInfo methodInfo = type.GetMethod("Write", BindingFlags.Instance | BindingFlags.Public);
            if (methodInfo != null && (option & WriteStructureOption.SkipWriteMethod) == 0)
            {
                methodInfo.Invoke(instance, new object[] { s, encrypter });
                return;
            }

            // Neither of those methods worked.
            // We'll try to write out the structure driven by the fields of the structure itself

            var fieldInfos = buildOrderedFieldList(instance.GetType());
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
                    string value = (string)field.GetValue(instance);
                    if (value == null)
                        continue;
                    if (encoding == typeof(ASCIIEncoding))
                        Write_String(s, encrypter, value);
                    else if (encoding == typeof(UnicodeEncoding))
                        Write_WString(s, encrypter, value);
                    else
                        throw new Exception("Bad structure declaration!");
                }
                else if (isBasicType(field.FieldType))
                {
                    Write_Basic_Type(field.GetValue(instance), s, encrypter);
                }
                else if (field.FieldType == typeof(byte[]))
                {
                    byte[] data = (byte[])field.GetValue(instance);
                    if (data == null)
                        continue;
                    Write_Bytes(s, encrypter, data);
                }
                else if (field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    // What kind of items do we want to read?
                    Type itemType = field.FieldType.GetGenericArguments()[0];

                    // Where are we getting the items from?
                    dynamic list = field.GetValue(instance);

                    //Console.WriteLine("Writing Substructure: {0}, count = {1}", itemType, list.Count);

                    // How will we write the item?
                    bool basicType = isBasicType(itemType);

                    // Write in the number of items we're about to write
                    //
                    // Only do this if we're supposed to be writing a list of a dynamic length
                    // For lists/arrays with a static length (StaticCount attribute attached), this means
                    // the reading logic will assume it knows how many items to read.
                    // For those types of items, skip writing the list length.
                    StaticCount count = (StaticCount)field.GetCustomAttribute(typeof(StaticCount));
                    if (count == null)
                        Write_UInt32(s, encrypter, (uint)list.Count);

                    // Start writing
                    for (int i = 0; i < list.Count; i++)
                    {
                        dynamic item = list[i];
                        writeStructure(item, s, encrypter);
                    }
                }
                else 
                    throw new Exception("I don't know how to handle this type of field!");

            }
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

        static void writeBlockLength(Stream s, Encrypter encrypter, long blockStartPos, long blockEndPos, uint encState)
        {
            // Save the current position
            long curPos = s.Position;

            // Go back to where we should write the length field
            s.Seek(blockStartPos, SeekOrigin.Begin);

            //Write the length
            s.Write(BitConverter.GetBytes((blockEndPos - blockStartPos - 4) ^ encState), 0, 4);

            // Restore the stream position
            s.Seek(curPos, SeekOrigin.Begin);
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

        // Given a block "object", figure out what the block ID should be when written to disk
        static UInt32 blockGetID(object block)
        {
            Type t = block.GetType();
            if (t.Name.StartsWith("Block"))
                return Convert.ToUInt32(t.Name.Substring(5));
            else
                return 0xffffffff;
        }

        static void writeBlock(object block, Stream s, Encrypter encrypter)
        {
            // Write the ID of the block to be written
            Write_UInt32(s, encrypter, blockGetID(block));

            // Write the dummy length of the block
            // We'll come back and fill this in later
            long blockStartPos = s.Position;
            uint blockStartEncState = encrypter.state;
            s.Write(BitConverter.GetBytes(0), 0, 4);

            // Write out the structure
            // Alternatively, we may be able to just go through an encryption pass on the plaintext data
            writeStructure(block, s, encrypter);

            // Now that we've written the data, go back and fill in the length field
            writeBlockLength(s, encrypter, blockStartPos, s.Position, blockStartEncState);

            // Write out the encrypter state for stream sync checks
            s.Write(BitConverter.GetBytes(encrypter.state), 0, 4);
        }

        class LoadFileInfo
        {
            public UInt32 seed;
            public UInt32 headerVersion;
            public UInt32 dataVersion;
            public byte[] mysteryField;
        }

        static Dictionary<string, object> loadCharacterFile(string filepath)
        {
            List<object> blockList = new List<object>();
            var loadInfo = new LoadFileInfo();

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
                UInt32 seed = reader.ReadUInt32() ^ 1431655765U;
                Encrypter enc = new Encrypter(seed);
                loadInfo.seed = seed;

                // Try to read the file marker ("GDCX")
                if (Read_UInt32(fs, enc) != 0x58434447)
                    throw new Exception("Incorrect magic ID read!");

                uint headerVersion = Read_UInt32(fs, enc);
                if (headerVersion != 1)
                    throw new Exception(String.Format("Incorrect header version!  Unknown version {0}", headerVersion));
                loadInfo.headerVersion = headerVersion;

                Header header = (Header)readStructure(typeof(Header), fs, enc);
                blockList.Add(header);

                uint checksum = reader.ReadUInt32();
                if (checksum != enc.state)
                    throw new Exception("Checksum mismatch!");

                uint dataVersion = Read_UInt32(fs, enc);
                if (dataVersion != 6 && dataVersion != 7)
                    throw new Exception(String.Format("Incorrect data version!  Unknown version {0}.", dataVersion));
                loadInfo.dataVersion = dataVersion;

                byte[] mysteryField = Read_Bytes(fs, enc, 16);
                loadInfo.mysteryField = mysteryField;


                // TODO!!! It might be a good idea to peak at the file to determine type of the next block to be read
                // instead of relying on a specific order.
                Block1 block1 = (Block1)readBlock(1, typeof(Block1), fs, enc);
                blockList.Add(block1);
                Block2 block2 = (Block2)readBlock(2, typeof(Block2), fs, enc);
                blockList.Add(block2);
                Block3 block3 = (Block3)readBlock(3, typeof(Block3), fs, enc);
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

            // Hold on to the block list and other misc infomration.
            // We're likely to need this when we serialize the character out to a file again.
            character["meta-blockList"] = blockList;
            character["meta-loadInfo"] = loadInfo;

            return character;
        }

        static void writeBlocksInOrder(List<Type> blockWriteOrder, List<object> blockList, Stream s, Encrypter encrypter)
        {
            foreach (var blockType in blockWriteOrder)
            {
                // Find a block in the block list of the correct type
                object block = blockList.Find(x => x.GetType() == blockType);
                writeBlock(block, s, encrypter);
            }
        }

        static bool writeCharacterFile(string filepath, Dictionary<string, dynamic> character)
        {
            // Open given file and truncate all existing content
            using (FileStream fs = new FileStream(filepath, FileMode.Create, FileAccess.Write))
            {
                if (fs == null)
                    return false;

                LoadFileInfo loadInfo = character["meta-loadInfo"];
                uint seed = loadInfo.seed;
                fs.Write(BitConverter.GetBytes(seed ^ 1431655765U), 0, 4);

                Encrypter enc = new Encrypter(seed);
                Write_UInt32(fs, enc, 0x58434447);

                //--------------------------------------------------------------
                // Header block
                Write_UInt32(fs, enc, loadInfo.headerVersion);

                List<object> blockList = character["meta-blockList"];
                Header header = (Header) blockList.Find(x => x.GetType() == typeof(Header));
                writeStructure(header, fs, enc);

                fs.Write(BitConverter.GetBytes(enc.state), 0, 4);
                // Header block
                //--------------------------------------------------------------

                Write_UInt32(fs, enc, loadInfo.dataVersion);
                Write_Bytes(fs, enc, loadInfo.mysteryField);

                // All the preambles are written
                // Now we start writing all the known blocks
                var blockWriteOrder = new List<Type>()
                {
                    typeof(Block1),
                    typeof(Block2),
                    typeof(Block3),
                    typeof(Block4),
                    typeof(Block5),
                    typeof(Block6),
                    typeof(Block7),
                    typeof(Block17),
                    typeof(Block8),
                    typeof(Block12),
                    typeof(Block13),
                    typeof(Block14),
                    typeof(Block15),
                    typeof(Block16),
                    typeof(Block10),
                };
                writeBlocksInOrder(blockWriteOrder, blockList, fs, enc);
            }
            return true;
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
