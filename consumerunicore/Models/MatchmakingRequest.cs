namespace consumerunicore.Models
{
    public class MatchmakingRequest
    {
        public string Region { get; set; } = string.Empty;
        public int CpuCoresNeeded { get; set; }
        public double RamGbNeeded { get; set; }
    }
}
