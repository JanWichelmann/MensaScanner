using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MensaScanner
{
    /// <summary>
    /// Currently supported menus:
    ///     - Mensa
    ///     - UKSH Bistro
    /// 
    /// Needs Xpdf's pdftotext utility in the PATH!
    ///     http://www.xpdfreader.com/download.html
    /// </summary>
    class Program
    {
        // URL constants.
        static string MensaUrl = @"https://www.studentenwerk.sh/de/essen/standorte/luebeck/mensa-luebeck/speiseplan.html";
        static string UkshUrl = @"https://www.uksh.de/uksh_media/Speisepl%C3%A4ne/L%C3%BCbeck+_+UKSH_Bistro/Speiseplan+Bistro+KW+{0:00}.pdf";

        /// <summary>
        /// Culture data for german strings and calender.
        /// </summary>
        static CultureInfo CultureGerman = new CultureInfo("de-DE");

        static void Main(string[] args)
        {
            for(int d = 11; d <= 14; ++d)
            {
                Console.WriteLine("\n\n\n######### Day " + d + " ##########");

                Console.WriteLine();
                Console.WriteLine("MENSA:");
                var mensaMenu = ParseMensa(new DateTime(2018, 10, d));
                foreach(var menuEntry in mensaMenu)
                    Console.WriteLine(menuEntry.Name + "    ||    " + menuEntry.Price);

                Console.WriteLine();
                Console.WriteLine("UKSH:");
                var uhshMenu = ParseUksh(new DateTime(2018, 10, d));
                foreach(var menuEntry in uhshMenu)
                    Console.WriteLine(menuEntry.Name + "    ||    " + menuEntry.Price);
            }
            Console.ReadLine();
        }

        static IEnumerable<MenuEntry> ParseUksh(DateTime date)
        {
            // Determine week number
            int week = CultureGerman.Calendar.GetWeekOfYear(date, CultureGerman.DateTimeFormat.CalendarWeekRule, DayOfWeek.Monday);
            string url = String.Format(UkshUrl, week);

            // Determine name of day
            string day = CultureGerman.DateTimeFormat.GetDayName(date.DayOfWeek);

            // Download PDF
            string workDir = Path.GetTempPath();
            string pdfFileName = Path.Combine(workDir + $"bistro{week}.pdf");
            using(HttpWebResponse response = (HttpWebResponse)WebRequest.Create(url).GetResponse())
            using(Stream stream = response.GetResponseStream())
            using(FileStream pdfFile = File.Open(pdfFileName, FileMode.Create, FileAccess.Write))
                stream.CopyTo(pdfFile);

            // Convert PDF into text format
            string txtFileName = Path.Combine(workDir + $"bistro{week}.txt");
            ProcessStartInfo pdfToTextProcConfig = new ProcessStartInfo
            {
                FileName = "pdftotext",
                Arguments = $"-layout bistro{week}.pdf bistro{week}.txt",
                WorkingDirectory = workDir,
                UseShellExecute = true,
                RedirectStandardError = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
            };
            Process.Start(pdfToTextProcConfig).WaitForExit();
            string txtFile = File.ReadAllText(txtFileName);


            // Find row for current day
            int row = 0;
            string[] lines = txtFile.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for(; row < lines.Length; ++row)
                if(lines[row].StartsWith(day, StringComparison.CurrentCultureIgnoreCase))
                    break;

            // Find row containing prices
            int priceRow = row;
            for(; priceRow < lines.Length; ++priceRow)
                if(lines[priceRow].Contains("€"))
                    break;

            // Determine column bounds
            List<(int Index, int Length)> columnBounds = new List<(int index, int length)>();
            foreach(Match m in Regex.Matches(lines[priceRow], "€.*?/.*?€.*?kJ [0-9]+"))
                columnBounds.Add((m.Index, m.Length));

            // Break day menu into cells
            string[,] dayMenuTable = new string[4, columnBounds.Count]; // Exactly 4 lines (including empty ones)
            for(int dayRow = 0; dayRow < 4; ++dayRow)
            {
                // Split row
                string line = lines[row + dayRow];
                for(int i = 0; i < columnBounds.Count; ++i)
                {
                    // Calculate split positions (exclusive)
                    int cellColStart = columnBounds[i].Index;
                    int cellColEnd = columnBounds[i].Index + columnBounds[i].Length;
                    if(cellColEnd > line.Length)
                        cellColEnd = line.Length;
                    int cellColWidth = cellColEnd - cellColStart;
                    if(cellColWidth > 0)
                        dayMenuTable[dayRow, i] = line.Substring(cellColStart, cellColWidth).Trim();
                }
            }

            // Create menu entries
            for(int c = 0; c < columnBounds.Count; ++c)
            {
                // Concat entry lines
                string menuEntryName = "";
                bool first = true;
                for(int r = 0; r < 3; ++r)
                    if(dayMenuTable[r, c] != null)
                    {
                        // Comma separate entry lines
                        if(first)
                            first = false;
                        else
                            menuEntryName += ", ";
                        menuEntryName += dayMenuTable[r, c];
                    }

                // Convert price cell
                string menuEntryPrice = Regex.Match(dayMenuTable[3, c], "€.*?/.*?€ *[0-9,]+").Value;

                // Add menu entry to returned list
                yield return new MenuEntry { Name = menuEntryName, Price = menuEntryPrice };
            }
        }

        static IEnumerable<MenuEntry> ParseMensa(DateTime date)
        {
            // Download HTML page
            string html;
            using(HttpWebResponse response = (HttpWebResponse)WebRequest.Create(MensaUrl).GetResponse())
            using(Stream stream = response.GetResponseStream())
            using(StreamReader reader = new StreamReader(stream))
                html = reader.ReadToEnd();

            // Find section for current day
            string dateString = date.ToString("yyyy-MM-dd");
            var dayMenuMatch = Regex.Match(html, $"<div.*?longdesc=\"{dateString}\".*?>(.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if(!dayMenuMatch.Success)
            {
                // Stop with error
                Console.WriteLine("Mensa: Unable to find day entry in menu. Layout changed or invalid date?");
                yield break;
            }
            string dayMenu = dayMenuMatch.Groups[1].Value;

            // Parse table rows
            bool first = true;
            foreach(Match dayMenuRowMatch in Regex.Matches(dayMenu, "<tr.*?>(.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                // Discard the table head
                if(first)
                {
                    first = false;
                    continue;
                }

                // There should be three columns
                string dayMenuRow = dayMenuRowMatch.Groups[1].Value;
                var dayMenuRowColumns = Regex.Matches(dayMenuRow, "<td.*?>(.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if(dayMenuRowColumns.Count != 3)
                {
                    // Stop with error
                    Console.WriteLine("Mensa: Menu row contains wrong number of cells.");
                    yield break;
                }

                // Extract menu entry name
                string menuEntryName = Regex.Match(dayMenuRowColumns[0].Groups[1].Value, "<strong>(.*?)</strong>", RegexOptions.IgnoreCase | RegexOptions.Singleline).Groups[1].Value.Trim();

                // Extract menu entry price
                string menuEntryPrice = dayMenuRowColumns[2].Groups[1].Value.Trim();

                // Add menu entry to returned list
                yield return new MenuEntry { Name = menuEntryName, Price = menuEntryPrice };
            }
        }
    }
}
