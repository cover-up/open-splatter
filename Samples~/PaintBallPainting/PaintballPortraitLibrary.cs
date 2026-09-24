using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CoverUp.Splatter.Samples
{
    /// <summary>
    /// Pictures the portrait can aim at. Anything in <see cref="Folder"/> ships with a player
    /// build and shows up in the list. In the editor the list also includes other sizeable png
    /// and jpeg files under Assets. Loading a file in the editor copies it into that folder and
    /// sets the importer: readable, non-power-of-two left alone, max size at least the source.
    /// Loading a file in a build reads the png or jpeg straight into a texture.
    /// </summary>
    public static class PaintballPortraitLibrary
    {
        public const string Folder = "Assets/Resources/PaintballPortraits";
        public const string ResourcePath = "PaintballPortraits";

        static readonly List<Texture2D> loaded = new List<Texture2D>();

        /// <summary>Files opened from disk during this run of a build. The editor copies those
        /// into <see cref="Folder"/> instead, so this stays empty there.</summary>
        public static IReadOnlyList<Texture2D> Loaded => loaded;

        /// <summary>Portraits packed into the build from <see cref="Folder"/>.</summary>
        public static Texture2D[] Bundled() => Resources.LoadAll<Texture2D>(ResourcePath);

        /// <summary>File window, then a texture read from that file. Null when the window is
        /// cancelled or the file is not a png or jpeg. Used by player builds.</summary>
        public static Texture2D LoadFromDisk()
        {
            string path = PickPath();
            if (string.IsNullOrEmpty(path)) return null;
            var tex = ReadFile(path);
            if (tex != null) loaded.Add(tex);
            return tex;
        }

        /// <summary>Save window for a PNG. Null when the window is cancelled. Used by player builds.</summary>
        public static string SavePngPath(string defaultName)
        {
            string safe = string.IsNullOrEmpty(defaultName) ? "paintball" : defaultName;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c.ToString(), "");
            if (string.IsNullOrEmpty(safe)) safe = "paintball";
            if (!safe.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) safe += ".png";
            string dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyPictures);
            if (string.IsNullOrEmpty(dir)) dir = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            string suggested = string.IsNullOrEmpty(dir) ? safe : Path.Combine(dir, safe);
            string path = PickSave(suggested);
            if (string.IsNullOrEmpty(path)) return null;
            if (!path.EndsWith(".png", System.StringComparison.OrdinalIgnoreCase)) path += ".png";
            return path;
        }

        /// <summary>Decode a png or jpeg. The texture is readable and keeps the file's pixel size.</summary>
        public static Texture2D ReadFile(string absolute)
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(absolute); }
            catch (IOException e)
            {
                Debug.LogError("PaintballPortrait: could not read " + absolute + ". " + e.Message);
                return null;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            if (!ImageConversion.LoadImage(tex, bytes, false))
            {
                Object.Destroy(tex);
                Debug.LogError("PaintballPortrait: " + absolute + " is not a png or jpeg.");
                return null;
            }
            tex.name = Path.GetFileNameWithoutExtension(absolute);
            tex.wrapMode = TextureWrapMode.Clamp;
            return tex;
        }

#if UNITY_EDITOR
        public readonly struct Entry
        {
            public readonly string Path, Label;
            public Entry(string path, string label) { Path = path; Label = label; }
        }

        /// <summary>Project pictures worth listing, by label. <paramref name="current"/> is included
        /// even when it lives outside the usual filter.</summary>
        public static List<Entry> List(Texture2D current)
        {
            var paths = new List<string>();
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Include(path)) paths.Add(path);
            }
            string currentPath = current != null ? AssetDatabase.GetAssetPath(current) : null;
            if (!string.IsNullOrEmpty(currentPath) && !paths.Contains(currentPath)) paths.Add(currentPath);
            paths.Sort((a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), System.StringComparison.OrdinalIgnoreCase));

            var counts = new Dictionary<string, int>();
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
            }
            var entries = new List<Entry>(paths.Count);
            foreach (string path in paths)
            {
                string name = Path.GetFileNameWithoutExtension(path);
                string label = counts[name] > 1 ? path.Substring("Assets/".Length) : name;
                entries.Add(new Entry(path, label));
            }
            return entries;
        }

        /// <summary>File panel. A picture already in the project is fixed in place. One from
        /// outside is copied into <see cref="Folder"/>. Null when the panel is cancelled.</summary>
        public static Texture2D ImportExternal()
        {
            string picked = EditorUtility.OpenFilePanelWithFilters("Load portrait", "", new[] { "Images", "png,jpg,jpeg" });
            if (string.IsNullOrEmpty(picked)) return null;
            return ImportPath(picked);
        }

        static Texture2D ImportPath(string picked)
        {
            string assetPath = ToAssetPath(picked);
            if (assetPath == null)
            {
                EnsureFolder();
                string file = Path.GetFileName(picked);
                assetPath = Folder + "/" + file;
                File.Copy(picked, Abs(assetPath), true);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            return Prepare(assetPath);
        }

        /// <summary>Make an imported texture readable and keep its real pixel size, then reload it.</summary>
        public static Texture2D Prepare(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError("PaintballPortrait: " + assetPath + " is not a texture.");
                return null;
            }
            int maxSize = 8192;
            if (TrySize(Abs(assetPath), out int w, out int h))
                maxSize = Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(w, h)), 32, 16384);

            bool dirty = !importer.isReadable
                || importer.npotScale != TextureImporterNPOTScale.None
                || importer.maxTextureSize < maxSize;
            if (dirty)
            {
                importer.isReadable = true;
                importer.npotScale = TextureImporterNPOTScale.None;
                importer.maxTextureSize = Mathf.Max(importer.maxTextureSize, maxSize);
                importer.sRGBTexture = true;
                importer.SaveAndReimport();
            }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
            if (tex == null || !tex.isReadable)
            {
                Debug.LogError("PaintballPortrait: " + assetPath + " did not come in readable.");
                return null;
            }
            return tex;
        }

        static bool Include(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return false;
            if (path.Contains("/Editor/")) return false;
            string slash = path.Replace('\\', '/');
            if (slash.StartsWith(Folder + "/")) return true;
            var importer = AssetImporter.GetAtPath(path) as TextureImporter;
            if (importer == null) return false;
            if (importer.textureType != TextureImporterType.Default && importer.textureType != TextureImporterType.Sprite) return false;
            if (!TrySize(Abs(path), out int w, out int h)) return false;
            return Mathf.Max(w, h) >= 256;
        }

        static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            if (!AssetDatabase.IsValidFolder(Folder))
                AssetDatabase.CreateFolder("Assets/Resources", "PaintballPortraits");
        }

        static string ToAssetPath(string absolute)
        {
            string full = Path.GetFullPath(absolute);
            string assets = Path.GetFullPath(Application.dataPath);
            if (!full.StartsWith(assets)) return null;
            string rel = full.Substring(assets.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "Assets/" + rel.Replace('\\', '/');
        }

        static string Abs(string assetPath)
        {
            string root = Directory.GetParent(Application.dataPath).FullName;
            return Path.GetFullPath(Path.Combine(root, assetPath));
        }

        static bool TrySize(string absolute, out int w, out int h)
        {
            w = h = 0;
            try
            {
                using var stream = File.OpenRead(absolute);
                string ext = Path.GetExtension(absolute).ToLowerInvariant();
                if (ext == ".png") return Png(stream, out w, out h);
                if (ext == ".jpg" || ext == ".jpeg") return Jpeg(stream, out w, out h);
            }
            catch (IOException) { }
            return false;
        }

        static bool Png(Stream stream, out int w, out int h)
        {
            w = h = 0;
            var buf = new byte[24];
            if (stream.Read(buf, 0, 24) < 24) return false;
            w = (buf[16] << 24) | (buf[17] << 16) | (buf[18] << 8) | buf[19];
            h = (buf[20] << 24) | (buf[21] << 16) | (buf[22] << 8) | buf[23];
            return w > 0 && h > 0;
        }

        static bool Jpeg(Stream stream, out int w, out int h)
        {
            w = h = 0;
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8) return false;
            var buf = new byte[8];
            while (true)
            {
                if (stream.ReadByte() != 0xFF) return false;
                int marker;
                do { marker = stream.ReadByte(); if (marker < 0) return false; } while (marker == 0xFF);
                if (marker == 0xD9 || marker == 0xDA) return false;
                if (stream.Read(buf, 0, 2) < 2) return false;
                int len = (buf[0] << 8) | buf[1];
                if (len < 2) return false;
                bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                if (sof)
                {
                    if (stream.Read(buf, 0, 5) < 5) return false;
                    h = (buf[1] << 8) | buf[2];
                    w = (buf[3] << 8) | buf[4];
                    return w > 0 && h > 0;
                }
                if (stream.CanSeek) stream.Seek(len - 2, SeekOrigin.Current);
                else return false;
            }
        }
#endif

        static string PickPath()
        {
#if UNITY_STANDALONE_LINUX
            return Run("zenity", "--file-selection", "--title=Load portrait", "--file-filter=Images | *.png *.jpg *.jpeg");
#elif UNITY_STANDALONE_OSX
            return Run("osascript", "-e", "try\nPOSIX path of (choose file with prompt \"Load portrait\" of type {\"public.png\", \"public.jpeg\"})\non error number -128\n\"\"\nend try");
#elif UNITY_STANDALONE_WIN
            return Run("powershell", "-NoProfile", "-STA", "-Command",
                "Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.OpenFileDialog; $d.Filter = 'Images|*.png;*.jpg;*.jpeg'; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.FileName) }");
#else
            Debug.LogError("PaintballPortrait: this player has no file window.");
            return null;
#endif
        }

        static string PickSave(string suggested)
        {
#if UNITY_STANDALONE_LINUX
            return Run("zenity", "--file-selection", "--save", "--confirm-overwrite", "--title=Export painting", "--filename=" + suggested, "--file-filter=PNG | *.png");
#elif UNITY_STANDALONE_OSX
            string name = Path.GetFileName(suggested);
            string dir = Path.GetDirectoryName(suggested) ?? "";
            string script = "try\nPOSIX path of (choose file name with prompt \"Export painting\" default name \"" + Apple(name) + "\" default location POSIX file \"" + Apple(dir) + "\")\non error number -128\n\"\"\nend try";
            return Run("osascript", "-e", script);
#elif UNITY_STANDALONE_WIN
            string psName = suggested.Replace("'", "''");
            return Run("powershell", "-NoProfile", "-STA", "-Command",
                "Add-Type -AssemblyName System.Windows.Forms; $d = New-Object System.Windows.Forms.SaveFileDialog; $d.Filter = 'PNG|*.png'; $d.FileName = '" + psName + "'; $d.Title = 'Export painting'; if ($d.ShowDialog() -eq 'OK') { [Console]::Out.Write($d.FileName) }");
#else
            Debug.LogError("PaintballPortrait: this player has no file window.");
            return null;
#endif
        }

        static string Apple(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string Run(string file, params string[] args)
        {
            try
            {
                var info = new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(info);
                if (process == null) return null;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0) return null;
                output = output.Trim();
                return output.Length == 0 ? null : output;
            }
            catch (System.Exception e)
            {
                Debug.LogError("PaintballPortrait: could not open a file window. " + e.Message);
                return null;
            }
        }
    }
}
