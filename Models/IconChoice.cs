using System.Windows.Media;

namespace Revenant_Theme_Studio.Models
{
    public class IconChoice
    {
        public required string ResourcePath { get; init; }
        public int ResourceIndex { get; init; }
        public required string DisplayName { get; init; }
        public required ImageSource PreviewImage { get; init; }
    }
}
