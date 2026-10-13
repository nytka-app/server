using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;

var cal = new Ical.Net.Calendar();
var ev = new CalendarEvent { Uid = "u", Summary = "mix\\,end", DtStamp = new CalDateTime(DateTime.UtcNow), DtStart = new CalDateTime(DateTime.UtcNow) };
cal.Events.Add(ev);
var text = new CalendarSerializer(cal).SerializeToString();
var line = text.Split("\r\n").First(l => l.StartsWith("SUMMARY"));
Console.WriteLine("len=" + line.Length);
foreach (var ch in line) Console.Write((int)ch + " ");
Console.WriteLine();
