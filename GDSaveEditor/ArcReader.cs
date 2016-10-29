using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GDSaveEditor
{
    class Arc_V3_Header
    {
        public uint magic;              // "ARC"
        public uint version;
        public int numberOfFileEntries;
        public int numberOfDataRecords; // (NumberOfDataRecords / 12) = RecordTableSize
        public uint recordTableSize;
        public uint stringTableSize;
        public uint recordTableOffset;
    }

    struct ArcRecordHeader
    {
        public uint entryType;
        public uint fileOffset;
        public int compressedSize;
        public int decompressedSize;
        public uint decompressedHash;   // Adler32 hash of the decompressed file bytes
        public UInt64 fileTime;
        public uint fileParts;
        public uint firstPartIndex;
        public int stringEntryLength;
        public uint stringEntryOffset;
    }

    struct ArcFilePartHeader
    {
        public uint offset;
        public int compressedSize;
        public int decompressedSize;
    };


    class ArcReader
    {

        public static Dictionary<string, byte[]> read(string filepath)
        {
            using (FileStream fs = new FileStream(filepath, FileMode.Open, FileAccess.Read))
            {
                // Check on the file header
                Arc_V3_Header header = (Arc_V3_Header)Program.readStructure(typeof(Arc_V3_Header), fs, null);
                if (header.magic != 0x435241 || header.version != 0x3)
                    throw new Exception("I don't understand this ARC format!");

                // Read out the records table
                var recordsTable = readRecordHeadersTable(fs, header.recordTableOffset + header.recordTableSize + header.stringTableSize, header.numberOfFileEntries);

                // Read out the contents of each "record" and return them in a map associated with their filename
                var arcContents = new Dictionary<string, byte[]>();
                foreach (var recordHeader in recordsTable)
                {
                    string filename = readRecordFilename(fs, header, recordHeader);
                    byte[] contents = readRecord(fs, header, recordHeader);
                    arcContents[filename] = contents;
                }

                return arcContents;
            }
        }

        internal static string readRecordFilename(Stream s, Arc_V3_Header header, ArcRecordHeader recordHeader)
        {
            BinaryReader reader = new BinaryReader(s);
            s.Seek(header.recordTableOffset + header.recordTableSize + recordHeader.stringEntryOffset, SeekOrigin.Begin);
            return Encoding.ASCII.GetString(reader.ReadBytes(recordHeader.stringEntryLength));
        }

        internal static byte[] readRecord(Stream s, Arc_V3_Header header, ArcRecordHeader recordHeader)
        {
            BinaryReader reader = new BinaryReader(s);

            s.Seek(recordHeader.fileOffset, SeekOrigin.Begin);

            // If the file contents isn't compressed, just read and return the contents now
            if (recordHeader.entryType == 1 && recordHeader.compressedSize == recordHeader.decompressedSize)
                return reader.ReadBytes(recordHeader.decompressedSize);

            // The file content is compressed...
            MemoryStream contentsStream = new MemoryStream();
            BinaryWriter contentWriter = new BinaryWriter(contentsStream);

            for (int partCursor = 0; partCursor < recordHeader.fileParts; partCursor++) {
                // Goto the beginning of the file part
                // Magic number "12" is the size of the ArcFilePart on disk
                s.Seek(header.recordTableOffset + (recordHeader.firstPartIndex + partCursor) * 12, SeekOrigin.Begin);

                // Read out the file part header
                var partHeader = (ArcFilePartHeader)Program.readStructure(typeof(ArcFilePartHeader), s, null);

                // Read out the part content
                s.Seek(partHeader.offset, SeekOrigin.Begin);
                byte[] contentBytes = reader.ReadBytes(partHeader.compressedSize);

                // If the content isn't compressed, just write it out
                if (partHeader.compressedSize == partHeader.decompressedSize)
                {
                    contentWriter.Write(contentBytes);
                    continue;
                }

                // If the content *is* compressed, decompress before writing it out
                byte[] decompressedBytes = LZ4.LZ4Codec.Decode(contentBytes, 0, contentBytes.Length, partHeader.decompressedSize);
                contentWriter.Write(decompressedBytes);
            }
            return contentsStream.GetBuffer();
        }

        static List<ArcRecordHeader> readRecordHeadersTable(Stream s, long tableOffset, int entries)
        {
            var headers = new List<ArcRecordHeader>();

            s.Seek(tableOffset, SeekOrigin.Begin);
            for(int i = 0; i < entries; i++)
            {
                ArcRecordHeader header = (ArcRecordHeader)Program.readStructure(typeof(ArcRecordHeader), s, null);
                headers.Add(header);
            }

            return headers;
        }
    }
}
