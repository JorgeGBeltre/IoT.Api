using System.Text.RegularExpressions;

namespace IoT.Protocols.Routing;

public static class TopicPatternMatcher
{
    public static bool MatchesMqttTopic(string pattern, string topic)
    {
        if (pattern == topic) return true;
        if (pattern == "#") return true;

        var patternSegments = pattern.Split('/');
        var topicSegments = topic.Split('/');

        int p = 0;
        int t = 0;

        while (p < patternSegments.Length && t < topicSegments.Length)
        {
            if (patternSegments[p] == "#")
                return true;

            if (patternSegments[p] != "+" && patternSegments[p] != topicSegments[t])
                return false;

            p++;
            t++;
        }

        if (p < patternSegments.Length && patternSegments[p] == "#")
            return true;

        return p == patternSegments.Length && t == topicSegments.Length;
    }

    public static bool TryExtractParameters(string template, string actualTopic, out Dictionary<string, string> parameters)
    {
        parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var templateSegments = template.Split('/');
        var actualSegments = actualTopic.Split('/');

        if (templateSegments.Length != actualSegments.Length)
            return false;

        for (int i = 0; i < templateSegments.Length; i++)
        {
            string tSeg = templateSegments[i];
            string aSeg = actualSegments[i];

            if (tSeg.StartsWith('{') && tSeg.EndsWith('}'))
            {
                string paramName = tSeg.Substring(1, tSeg.Length - 2);
                parameters[paramName] = aSeg;
            }
            else if (!string.Equals(tSeg, aSeg, StringComparison.OrdinalIgnoreCase))
            {
                parameters.Clear();
                return false;
            }
        }

        return true;
    }
}
