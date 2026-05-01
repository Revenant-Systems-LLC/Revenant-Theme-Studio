namespace RTS.MunWriter
{
    public class MunJob
    {
        public string Target { get; set; } = "";
        public List<MunReplacement> Replacements { get; set; } = [];
    }

    public class MunReplacement
    {
        public int    GroupId { get; set; }
        public string IcoPath { get; set; } = "";
    }
}
