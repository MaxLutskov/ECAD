using System.Globalization;

namespace ECAD.Core;

// Computes a complete proposal before the session commits one undoable change.
public static class NetNumbering
{
    public static ProjectNet[] Renumber(IReadOnlyList<ProjectNet> nets, string prefix, int start, int step)
    {
        if (prefix is null || start < 0 || step < 1)
            throw new InvalidDataException("Некоректні параметри нумерації.");
        var reserved = nets.Where(net => net.NumberLocked).Select(net => net.Number)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        long next = start;
        var result = new ProjectNet[nets.Count];
        for (var index = 0; index < nets.Count; index++)
        {
            var net = nets[index];
            if (net.NumberLocked) { result[index] = net; continue; }
            string number;
            do
            {
                if (next > int.MaxValue) throw new InvalidDataException("Вичерпано діапазон номерів кіл.");
                number = prefix + next.ToString(CultureInfo.InvariantCulture);
                if (number.Length > 64) throw new InvalidDataException("Номер кола не може перевищувати 64 символи.");
                next += step;
            } while (!reserved.Add(number));
            result[index] = net.Number == number ? net : net with { Number = number };
        }
        return result;
    }
}
