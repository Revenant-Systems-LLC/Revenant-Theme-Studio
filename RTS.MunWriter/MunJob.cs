namespace RTS.MunWriter
{
    public class MunJob
    {
        public string Target { get; set; } = "";
        /// <summary>"replace" (patch icon groups) or "restore" (put the preserved Windows original back).</summary>
        public string Mode { get; set; } = "replace";
        public List<MunReplacement> Replacements { get; set; } = [];
    }

    public class MunReplacement
    {
        public int    GroupId { get; set; }
        public string IcoPath { get; set; } = "";
    }
}
