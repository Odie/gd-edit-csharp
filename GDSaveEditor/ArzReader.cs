using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GDSaveEditor
{
    class Arz_V3_Header
    {
        public ushort unknown;
        public ushort version;
        public uint recordTableStart;
        public uint recordTableSize;
        public uint recordTableEntries;
        public uint stringTableStart;
        public uint stringTableSize;
    }

    class ArzReader
    {
        List<String> stringTable;

        internal static void read(string filepath)
        {
            ArzReader arzReader = new ArzReader();

            using (FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read))
            {
                Arz_V3_Header header = (Arz_V3_Header)Program.readStructure(typeof(Arz_V3_Header), fs, null);
                if (header.unknown != 0x2 || header.version != 0x3)
                    throw new Exception("I don't understand this ARZ format!");

                var stringTable = readStringTable(fs, header.stringTableStart, header.stringTableStart + header.stringTableSize);

                fs.Seek(header.recordTableStart, SeekOrigin.Begin);
                System.IO.BinaryReader reader = new System.IO.BinaryReader(fs);
                for (int i = 0; i < header.recordTableEntries; i++)
                {
                    var filename = stringTable[reader.ReadInt32()];
                    Console.WriteLine("{0}: {1}", i, filename);
                    var typeLength = reader.ReadInt32();
                    var type = Encoding.ASCII.GetString(reader.ReadBytes(typeLength));
                    var dataOffset = reader.ReadUInt32();
                    var dataCompressedSize = reader.ReadUInt32();
                    var dataDecompressedSize = reader.ReadUInt32();
                    fs.Seek(8, SeekOrigin.Current); // There not sure what the next 8 bytes are for
                }
            }
        }

        // Read out a list of strings and puts them in a big array.
        // In C, a string table is typically one large byte buffer that's stuff full of stings. You can then
        // just build a list of pointers into that memory to avoid extra memory allocation per string.
        // We don't care memory efficiency here. We're just going to stick a bunch of strings into a big array.
        static List<string> readStringTable(Stream s, long tableStartOffset, long tableEndOffset)
        {
            // Jump to where the string table is in the file
            long origPos = s.Position;
            s.Seek(tableStartOffset, SeekOrigin.Begin);

            // Figure out how many strings there are in the table
            var stringTable = new List<string>();
            System.IO.BinaryReader reader = new System.IO.BinaryReader(s);
            uint stringCount = reader.ReadUInt32();

            // Read each string out of the table
            for(int i = 0; i < stringCount; i++)
            {
                var strlen = reader.ReadInt32();
                var bytes = reader.ReadBytes(strlen);
                var str = System.Text.Encoding.ASCII.GetString(bytes);
                stringTable.Add(str); 
            }

            if (s.Position != tableEndOffset)
                throw new Exception("String Table didn't end where it said it would!");

            s.Seek(origPos, SeekOrigin.Begin);

            return stringTable;
        }
    }
}
