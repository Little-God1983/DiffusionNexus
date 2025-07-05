using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.UI.Classes
{
    public class LogSeverityFilter
    {
        public LogSeverityFilter(string name, params LogSeverity[] severities)
        {
            Name = name;
            Severities = severities;
        }

        public string Name { get; }
        public LogSeverity[] Severities { get; }
        public override string ToString() => Name;
    }
}
