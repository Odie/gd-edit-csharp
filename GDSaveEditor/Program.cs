using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace GDSaveEditor
{
    class Program
    {
        static void Main(string[] args)
        {
            // Find all the save files!
            var saveFileDirs = findSaveFileDirs();

            // Display all save files
            foreach (var saveFileDir in saveFileDirs)
                Console.WriteLine(saveFileDir);
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
