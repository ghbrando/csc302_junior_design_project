namespace consumerunicore.Models
{
    public class MatchmakingResult
    {
        public Provider Provider { get; set; } = default!;
        public MachineSpecs? MachineSpecs { get; set; }
        public int AvailableCpuCores { get; set; }
        public double AvailableRamGb { get; set; }
        public double ConsistencyScore { get; set; }
    }
}
