using System.IO;

namespace Revenant_Theme_Studio.Services
{
    public class IconMatchingService
    {
        private readonly string[] _iconFolders;

        public IconMatchingService(params string[] iconFolders)
        {
            _iconFolders = iconFolders;
        }

		public List<string> GetAllIcons()
		{
			var results = new List<string>();
			foreach (var folder in _iconFolders)
			{
				if (!Directory.Exists(folder)) continue;
				results.AddRange(
					Directory.GetFiles(folder, "*.ico", SearchOption.AllDirectories)
							 .Where(f => !f.Contains(Path.DirectorySeparatorChar + "PNG" + Path.DirectorySeparatorChar))
				);
			}
			return results;
		}



        public string? FindBestMatch(string folderName)
        {
            var all = GetAllIcons();
            string lower = folderName.ToLower();

            // Exact match first
            var exact = all.FirstOrDefault(i =>
                Path.GetFileNameWithoutExtension(i).ToLower() == lower);
            if (exact != null) return exact;

            // Contains match
            var contains = all.FirstOrDefault(i =>
                Path.GetFileNameWithoutExtension(i).ToLower().Contains(lower) ||
                lower.Contains(Path.GetFileNameWithoutExtension(i).ToLower()));
            return contains;
        }
    }
}
