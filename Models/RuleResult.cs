using System.Collections.Generic;

namespace Coflnet.Payments.Models
{
    public class RuleResult
    {
        public Product ModifiedProduct { get; set; }
        public IEnumerable<Rule> Rules { get; set; }
    }
}