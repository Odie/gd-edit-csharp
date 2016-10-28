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

    class ArzRecordHeader
    {
        public string filename;
        public string type;
        public uint dataOffset;
        public int dataCompressedSize;
        public int dataDecompressedSize;
    }

    class ArzReader
    {
        internal static Dictionary<string, Dictionary<string, object>> read(string filepath)
        {
            var db = new Dictionary<string, Dictionary<string, object>>();

            using (FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read))
            {
                Arz_V3_Header header = (Arz_V3_Header)Program.readStructure(typeof(Arz_V3_Header), fs, null);
                if (header.unknown != 0x2 || header.version != 0x3)
                    throw new Exception("I don't understand this ARZ format!");

                var stringTable = readStringTable(fs, header.stringTableStart, header.stringTableStart + header.stringTableSize);
                var recordsTable = readRecordHeadersTable(fs, header.recordTableStart, (int)header.recordTableEntries, stringTable);

                var timer = System.Diagnostics.Stopwatch.StartNew();
                foreach(var recordHeader in recordsTable)
                {
                    var record = readRecord(fs, recordsTable[0], stringTable);
                    db[recordHeader.filename] = record;
                }
                timer.Stop();
                Console.WriteLine("{0} seconds to read the db", TimeSpan.FromMilliseconds(timer.ElapsedMilliseconds).Seconds);

                return db;
            }
        }

        internal static Dictionary<string, object> readRecord(Stream s, ArzRecordHeader recordHeader, List<string> stringTable, bool ignoreZeroedFields = true)
        {
            // Prep where we're going to store the compressed bytes, taken directly from the file
            byte[] compressedBytes = new byte[recordHeader.dataCompressedSize];

            // Go to the correct file location and read
            // FIXME!!! Magic number 24 is the size of the file header on disk.
            // It'd be really nice for CLR to be able to just give me that size so the number doesn't have to be hard coded here.
            s.Seek(recordHeader.dataOffset + 24, SeekOrigin.Begin);
            s.Read(compressedBytes, 0, recordHeader.dataCompressedSize);

            // Decompress the data
            byte[] decompressedBytes = LZ4.LZ4Codec.Decode(compressedBytes, 0, compressedBytes.Length, recordHeader.dataDecompressedSize);

            MemoryStream recordStream = new MemoryStream(decompressedBytes);
            BinaryReader reader = new BinaryReader(recordStream);

            var record = new Dictionary<string, object>();

            while (recordStream.Position < recordStream.Length)
            {
                var dataType = reader.ReadUInt16();
                var dataCount = reader.ReadUInt16();
                var dataFieldnameIndex = reader.ReadInt32();

                var fieldName = stringTable[dataFieldnameIndex];

                for(int i = 0; i < dataCount; i++)
                {
                    object val = null;
                    switch(dataType)
                    {
                        case 0:
                        case 3:
                        default:
                            val = reader.ReadUInt32();
                            if (ignoreZeroedFields && (uint)val == 0)
                                continue;
                            break;

                        case 1:
                            val = reader.ReadSingle();
                            if (ignoreZeroedFields && (Single)val == 0.0)
                                continue;
                            break;
                        case 2:
                            val = stringTable[reader.ReadInt32()];
                            if (ignoreZeroedFields && (string)val == String.Empty)
                                continue;
                            break; 
                    }

                    record[fieldName] = val;
                }
            }

            return record;
        }

        static List<ArzRecordHeader> readRecordHeadersTable(Stream s, long tableStartOffset, int entries, List<string> stringTable)
        {
            var recordHeadersTable = new List<ArzRecordHeader>();

            s.Seek(tableStartOffset, SeekOrigin.Begin);
            System.IO.BinaryReader reader = new System.IO.BinaryReader(s);
            for (int i = 0; i < entries; i++)
            {
                var recordHeader = new ArzRecordHeader();

                recordHeader.filename = stringTable[reader.ReadInt32()];
                var typeLength = reader.ReadInt32();
                recordHeader.type = Encoding.ASCII.GetString(reader.ReadBytes(typeLength));
                recordHeader.dataOffset = reader.ReadUInt32();
                recordHeader.dataCompressedSize = reader.ReadInt32();
                recordHeader.dataDecompressedSize = reader.ReadInt32();
                s.Seek(8, SeekOrigin.Current); // There not sure what the next 8 bytes are for

                recordHeadersTable.Add(recordHeader);
            }

            return recordHeadersTable;
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
