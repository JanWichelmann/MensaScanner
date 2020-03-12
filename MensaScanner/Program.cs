using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        /// Path of the configuration file.
        /// </summary>
        static string ConfigFile = @"C:\Users\ITS\Daten\VisualStudio\Projects\MensaScanner\accesscodes.txt"; // PI: "accesscodes.txt";

        /// <summary>
        /// Culture data for german strings and calender.
        /// </summary>
        static CultureInfo CultureGerman = new CultureInfo("de-DE");

        /// <summary>
        /// Program entry point.
        /// </summary>
        /// <param name="args">Unused.</param>
        static void Main(string[] args)
        {
            // Load config file
            var config = ReadConfig();

            // Need to get authentication code?
            if(!config.ContainsKey("AuthCode"))
            {
                // Ask user for code
                Console.WriteLine("Please open the following URL and copy the final \"code\" parameter:");
                Console.WriteLine(config["URL"]);
                Console.Write("Code: ");
                string code = Console.ReadLine();
                config["AuthCode"] = code;
                SaveConfig(config);
                Console.WriteLine("Authentication code saved.");
            }
            // Protocol on https://developer.webex.com/authentication.html
            if(!config.ContainsKey("AccessToken"))
            {
                // Use authentication code to gain access token
                Console.WriteLine("Requesting initial access token...");
                JObject response;
                IEnumerable<KeyValuePair<string, string>> requestParams = new List<KeyValuePair<string, string>>
                {
                       new KeyValuePair<string, string>("grant_type", "authorization_code"),
                       new KeyValuePair<string, string>("client_id", config["ClientID"]),
                       new KeyValuePair<string, string>("client_secret", config["ClientSecret"]),
                       new KeyValuePair<string, string>("code", config["AuthCode"]),
                       new KeyValuePair<string, string>("redirect_uri", "http://invalid/"),
                };
                using(var httpClient = new HttpClient())
                using(var htmlForm = new FormUrlEncodedContent(requestParams))
                {
                    htmlForm.Headers.Clear();
                    htmlForm.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
                    string responseContentRaw = httpClient.PostAsync("https://api.ciscospark.com/v1/access_token", htmlForm).Result.Content.ReadAsStringAsync().Result;
                    response = JObject.Parse(responseContentRaw);
                }

                // Extract access token
                config["AccessToken"] = (string)response["access_token"];
                config["AccessTokenExpires"] = DateTime.Now.AddSeconds(double.Parse((string)response["expires_in"])).ToFileTimeUtc().ToString();
                config["RefreshToken"] = (string)response["refresh_token"];
                config["RefreshTokenExpires"] = DateTime.Now.AddSeconds(double.Parse((string)response["refresh_token_expires_in"])).ToFileTimeUtc().ToString();
                SaveConfig(config);
                Console.WriteLine("Requesting initial access token successful.");
            }

            // Access token expired?
            DateTime accessTokenExpires = DateTime.FromFileTimeUtc(long.Parse(config["AccessTokenExpires"]));
            if(accessTokenExpires < DateTime.Now + new TimeSpan(48, 0, 0)) // Refresh access token two days before it expires
            {
                // Request new access token
                Console.WriteLine("Requesting new access token...");
                JObject response;
                IEnumerable<KeyValuePair<string, string>> requestParams = new List<KeyValuePair<string, string>>
                {
                       new KeyValuePair<string, string>("grant_type", "refresh_token"),
                       new KeyValuePair<string, string>("client_id", config["ClientID"]),
                       new KeyValuePair<string, string>("client_secret", config["ClientSecret"]),
                       new KeyValuePair<string, string>("refresh_token", config["RefreshToken"]),
                };
                using(var httpClient = new HttpClient())
                using(var htmlForm = new FormUrlEncodedContent(requestParams))
                {
                    htmlForm.Headers.Clear();
                    htmlForm.Headers.Add("Content-Type", "application/x-www-form-urlencoded");
                    string responseContentRaw = httpClient.PostAsync("https://api.ciscospark.com/v1/access_token", htmlForm).Result.Content.ReadAsStringAsync().Result;
                    response = JObject.Parse(responseContentRaw);
                }

                // Extract access token
                // This also automatically renews the refresh token
                config["AccessToken"] = (string)response["access_token"];
                config["AccessTokenExpires"] = DateTime.Now.AddSeconds(double.Parse((string)response["expires_in"])).ToFileTimeUtc().ToString();
                config["RefreshToken"] = (string)response["refresh_token"];
                config["RefreshTokenExpires"] = DateTime.Now.AddSeconds(double.Parse((string)response["refresh_token_expires_in"])).ToFileTimeUtc().ToString();
                SaveConfig(config);
                Console.WriteLine("Requesting new access token successful.");
            }

            // Retrieve menus
            Console.WriteLine("Retrieving menus...");
            Console.WriteLine();
            Console.WriteLine("MENSA:");
            var mensaMenu = ParseMensa(DateTime.Today);
            foreach(var menuEntry in mensaMenu)
                Console.WriteLine($"- {menuEntry.Name}    [{menuEntry.Price}; {menuEntry.Properties}]");

            Console.WriteLine();
            Console.WriteLine("UKSH:");
            var ukshMenu = ParseUksh(DateTime.Today);
            foreach(var menuEntry in ukshMenu)
                Console.WriteLine($"- {menuEntry.Name}    [{menuEntry.Price}]");

            // Prepare message markdown
            Console.WriteLine("Preparing message...");
            StringBuilder message = new StringBuilder();
            message.AppendLine("**Mensa**");
            foreach(var menuEntry in mensaMenu)
                message.AppendLine($"- {menuEntry.Name}    [{menuEntry.Price}; {menuEntry.Properties}]");
            message.AppendLine();
            message.AppendLine("**UKSH Bistro**");
            foreach(var menuEntry in ukshMenu)
                message.AppendLine($"- {menuEntry.Name}    [{menuEntry.Price}]");

            // Send message
            Console.WriteLine("Posting message...");
            HttpWebRequest messagePostRequest = WebRequest.CreateHttp("https://api.ciscospark.com/v1/messages");
            {
                // Set HTTP headers
                messagePostRequest.Method = "POST";
                messagePostRequest.Headers.Clear();
                messagePostRequest.Headers.Add("Content-type: application/json; charset=utf-8");
                messagePostRequest.Headers.Add("Authorization: Bearer " + config["AccessToken"]);

                // Put message data into JSON object
                JObject messageData = new JObject
                {
                    { "roomId", config["RoomID"] },
                    { "markdown", message.ToString() }
                };

                // Set message as HTTP request content
                using(StreamWriter w = new StreamWriter(messagePostRequest.GetRequestStream()))
                    w.Write(messageData.ToString());

                // Get response
                using(HttpWebResponse messagePostResponse = (HttpWebResponse)messagePostRequest.GetResponse())
                    if(messagePostResponse.StatusCode == HttpStatusCode.OK)
                        Console.WriteLine("Posting message successful.");
                    else
                        Console.WriteLine("Posting message failed: " + (int)messagePostResponse.StatusCode);
            }


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
                FileName = "pdftotext", // PI: "/usr/bin/pdftotext"
                Arguments = $"-layout bistro{week}.pdf bistro{week}.txt",
                WorkingDirectory = workDir,
                UseShellExecute = true, // PI: false
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
            string[,] dayMenuTable = new string[4, columnBounds.Count]; // At most 4 lines
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
                // Find price line
                int priceLineNumber = 0;
                for(; priceLineNumber < dayMenuTable.GetLength(0); ++priceLineNumber)
                    if(dayMenuTable[priceLineNumber, c] != null && dayMenuTable[priceLineNumber, c].Contains("€"))
                        break;

                // Concat entry lines
                string menuEntryName = "";
                bool first = true;
                for(int r = 0; r < priceLineNumber; ++r)
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
                string menuEntryPrice = Regex.Match(dayMenuTable[priceLineNumber, c], "€.*?/.*?€ *[0-9,]+").Value;

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
                menuEntryName = Regex.Replace(menuEntryName, "(<small>.*?</small>)|(<br ?/?>)", "", RegexOptions.IgnoreCase);

                // Extract menu properties
                var menuProperties = Regex.Matches(dayMenuRowColumns[1].Groups[1].Value, "<img src=.*? alt=\"(.*?)\"/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

                string menuPropertiesText = "";
                foreach(Match prop in menuProperties)
                {
                    menuPropertiesText = menuPropertiesText + GetPropertyText(prop.Groups[1].Value) + ", ";
                }
                if(menuPropertiesText.Length > 2)
                    menuPropertiesText = menuPropertiesText.Substring(0, menuPropertiesText.Length - 2);

                // Extract menu entry price
                string menuEntryPrice = dayMenuRowColumns[2].Groups[1].Value.Trim();

                // Add menu entry to returned list
                yield return new MenuEntry { Name = menuEntryName, Price = menuEntryPrice, Properties = menuPropertiesText };
            }
        }

        static string GetPropertyText(string altText)
        {
            string modifiedAltText = altText.Trim();
            if(modifiedAltText == "")
            {
                modifiedAltText = "Geflügel";
            }
            modifiedAltText = modifiedAltText[0].ToString().ToUpper() + modifiedAltText.Substring(1);

            return modifiedAltText;
        }

        static Dictionary<string, string> ReadConfig()
        {
            // Read config file
            Dictionary<string, string> config = new Dictionary<string, string>();
            foreach(string line in File.ReadAllLines(ConfigFile))
            {
                // Split key and value
                int splitIndex = line.IndexOf(':');
                config.Add(line.Substring(0, splitIndex), line.Substring(splitIndex + 1));
            }
            return config;
        }

        static void SaveConfig(Dictionary<string, string> config)
        {
            // Write config file
            using(StreamWriter w = new StreamWriter(File.Open(ConfigFile, FileMode.Create)))
            {
                foreach(var entry in config)
                    w.WriteLine(entry.Key + ":" + entry.Value);
            }
        }
    }
}
