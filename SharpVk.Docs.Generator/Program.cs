using Octokit;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace SharpVk.Docs.Generator
{
    class Program
    {
        static void Main(string[] args)
        {
            string listFilePath = ".\\FileUriList.txt";

            if (!File.Exists(listFilePath) || File.GetLastWriteTimeUtc(listFilePath) + TimeSpan.FromDays(1) < DateTime.UtcNow)
            {
                Console.WriteLine("Get Contents List");

                var gitHubClient = new GitHubClient(new ProductHeaderValue("SharpVk"));

                var contents = gitHubClient.Repository.Content.GetAllContents("KhronosGroup", "Vulkan-Docs", "doc/specs/vulkan/chapters").Result;

                var fileList = new List<Uri>();

                fileList.AddRange(contents.Where(x => x.Type == ContentType.File).Select(x => x.DownloadUrl));

                foreach (var subDir in contents.Where(x => x.Type == ContentType.Dir))
                {
                    Console.WriteLine($"Get Subdirectory {subDir.Path}");

                    var subDirContents = gitHubClient.Repository.Content.GetAllContents("KhronosGroup", "Vulkan-Docs", subDir.Path).Result;

                    fileList.AddRange(subDirContents.Where(x => x.Type == ContentType.File).Select(x => x.DownloadUrl));
                }

                File.WriteAllLines(listFilePath, fileList.Select(x => x.ToString()));
            }

            Console.WriteLine("Get File Contents");

            var client = new HttpClient();

            var fileGets = File.ReadAllLines(listFilePath).Select(x =>
            {
                var fileUri = new Uri(x);

                string tempFolder = ".\\VkTemplates\\Chapters";

                string fileFolder = fileUri.Segments.Reverse().ElementAt(1).Trim('/');

                if (fileFolder != "chapters")
                {
                    tempFolder = Path.Combine(tempFolder, fileFolder);
                }

                return GetCachedFile(tempFolder, x);
            }).ToArray();

            var refLookup = new Dictionary<string, RefIndex>();
            var refEnd = new Dictionary<string, RefIndex>();

            var fileData = new Dictionary<string, string[]>();

            foreach (var file in fileGets)
            {
                string fileName = file.Result;

                var lines = File.ReadAllLines(fileName);

                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    var line = lines[lineIndex];

                    line = Regex.Replace(line, @"\[\[[a-zA-Z0-9\-]*\]\]", "");

                    lines[lineIndex] = line;
                }

                fileData.Add(fileName, lines);

                for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                {
                    string line = lines[lineIndex].Trim();

                    if (line.StartsWith("// ref"))
                    {
                        if (line.StartsWith("// refBegin"))
                        {
                            string subjectName = line.Substring(12);
                            subjectName = subjectName.Substring(0, subjectName.IndexOf(' '));

                            refLookup.Add(subjectName, new RefIndex
                            {
                                FileName = fileName,
                                LineIndex = lineIndex
                            });
                        }
                        else if (line.StartsWith("// refEnd"))
                        {
                            string subjectName = line.Substring(10);
                            if (subjectName.IndexOf(' ') > 0)
                            {
                                subjectName = subjectName.Substring(0, subjectName.IndexOf(' '));
                            }

                            refEnd.Add(subjectName, new RefIndex
                            {
                                FileName = fileName,
                                LineIndex = lineIndex
                            });
                        }
                    }
                }
            }

            var vkXml = XDocument.Load(GetCachedFile(".\\VkTemplates", "https://raw.githubusercontent.com/KhronosGroup/Vulkan-Docs/1.0/src/spec/vk.xml").Result);

            Console.WriteLine("Building Docs");

            var typeDocElements = new List<XElement>();

            var enumComments = vkXml.Element("registry")
                                        .Elements("enums")
                                        .ToDictionary(x => x.Attribute("name").Value, x => x.Elements("enum").ToDictionary(y => y.Attribute("name").Value, y => y.Attribute("comment")?.Value));

            foreach (var vkType in vkXml.Element("registry").Elements("types").Elements("type"))
            {
                string typeName = vkType.Attribute("name")?.Value ?? vkType.Element("name")?.Value;

                if (refLookup.ContainsKey(typeName))
                {
                    string summary;
                    IEnumerable<string> lines;

                    GetLines(refLookup, fileData, typeName, out summary, out lines);

                    var paragraphs = GetParagraphs(lines).ToArray();

                    var apiIncludeIndex = paragraphs.TakeWhile(x => !IsInclude(x, "api")).Count();

                    var specification = paragraphs.Take(apiIncludeIndex - 1);

                    var memberDocElements = new List<XElement>();

                    var validityIncludeIndex = paragraphs.TakeWhile(x => !IsInclude(x, "validity")).Count();

                    if (!paragraphs.Any(x => IsInclude(x, "validity")))
                    {
                        validityIncludeIndex = paragraphs.Count() + 1;
                    }

                    var descriptionParagraphs = paragraphs.Skip(apiIncludeIndex + 1).Take((validityIncludeIndex - apiIncludeIndex) - 1);

                    IEnumerable<string> memberList;
                    int memberParagraphCount;

                    if (descriptionParagraphs.Any() && TryGetMemberList(descriptionParagraphs, out memberList, out memberParagraphCount))
                    {
                        var memberItems = memberList.ToDictionary(item =>
                        {
                            var terms = item.Split(' ');

                            int memberTermIndex = terms.TakeWhile(term => !term.Contains(':')).Count();

                            string memberTerm = terms[memberTermIndex].Trim(':');

                            var memberNameParts = memberTerm.Split(':');

                            string memberName = memberNameParts.Last();

                            return new string(memberName.TakeWhile(character => char.IsLetterOrDigit(character) || character == '_' || character == '.').ToArray());
                        }, x => x);

                        descriptionParagraphs = descriptionParagraphs.Skip(memberParagraphCount);

                        var commentMappings = enumComments.ContainsKey(typeName) ? enumComments[typeName] : null;

                        if (commentMappings != null)
                        {
                            foreach (var mapping in commentMappings)
                            {
                                if (!memberItems.ContainsKey(mapping.Key))
                                {
                                    memberItems.Add(mapping.Key, mapping.Value);
                                }
                            }
                        }

                        foreach (var member in memberItems)
                        {
                            memberDocElements.Add(new XElement("member", new XAttribute("name", member.Key), member.Value));
                        }
                    }

                    var specificationParaElements = specification.Select(x => new XElement("para", string.Join(" ", x.Select(y => y.Trim())))).ToArray();
                    var descriptionParaElements = descriptionParagraphs.Select(x => new XElement("para", string.Join(" ", x.Select(y => y.Trim())))).ToArray();

                    typeDocElements.Add(new XElement("type",
                                            new XAttribute("name", typeName),
                                            new XAttribute("summary", summary),
                                            new XElement("specification", specificationParaElements),
                                            new XElement("description", descriptionParaElements),
                                            new XElement("members", memberDocElements.ToArray())));
                }
            }

            var commandDocElements = new List<XElement>();

            foreach (var vkCommand in vkXml.Element("registry").Elements("commands").Elements("command"))
            {
                string commandName = vkCommand.Element("proto").Element("name")?.Value;
                string summary;
                IEnumerable<string> lines;

                if (refLookup.ContainsKey(commandName))
                {
                    GetLines(refLookup, fileData, commandName, out summary, out lines);

                    commandDocElements.Add(new XElement("command", new XAttribute("name", commandName), new XAttribute("summary", summary)));
                }
            }

            var docXml = new XDocument(new XElement("docs",
                                        new XElement("types", typeDocElements.OrderBy(x => x.Attribute("name").Value).ToArray()),
                                        new XElement("commands", commandDocElements.OrderBy(x => x.Attribute("name").Value).ToArray())));

            string docFilePath = "..\\..\\..\\Docs\\vkDocs.xml";

            string docFileLocation = Path.GetDirectoryName(docFilePath);

            if (!Directory.Exists(docFileLocation))
            {
                Directory.CreateDirectory(docFileLocation);
            }

            docXml.Save(docFilePath);

            Console.WriteLine("Done");
            Console.ReadLine();
        }

        private static IEnumerable<IEnumerable<string>> GetParagraphs(IEnumerable<string> lines)
        {
            var segment = new List<string>();

            string previousValue = null;
            bool isInSubsection = false;

            foreach (var value in lines)
            {
                if (!isInSubsection && value == "--" && previousValue == "+")
                {
                    isInSubsection = true;

                    segment.Add(value);
                }
                else if (isInSubsection && value == "--")
                {
                    isInSubsection = false;

                    segment.Add(value);
                }
                else if (!isInSubsection && string.IsNullOrEmpty(value))
                {
                    if (segment.Any())
                    {
                        yield return segment.ToArray();

                        segment.Clear();
                    }
                }
                else
                {
                    segment.Add(value);
                }

                previousValue = value;
            }

            if (segment.Any())
            {
                yield return segment.ToArray();
            }
        }

        private static bool TryGetMemberList(IEnumerable<IEnumerable<string>> descriptionParagraphs, out IEnumerable<string> memberList, out int memberParagraphCount)
        {
            memberList = null;

            memberParagraphCount = 0;

            string initialLine = descriptionParagraphs.First().First();

            if (initialLine.StartsWith("  *"))
            {
                memberList = SplitList(descriptionParagraphs.First());

                memberParagraphCount = 1;

                return true;
            }
            else if (initialLine.Trim().StartsWith("ename") && initialLine.Trim().EndsWith("::"))
            {
                memberList = descriptionParagraphs.TakeWhile(x => x.First().Trim().StartsWith("ename")).Select(x => string.Join(" ", x.Select(y => y.Trim())));

                memberParagraphCount = memberList.Count();

                return true;
            }
            else if (descriptionParagraphs.Any(IsMemberList))
            {
                memberList = SplitList(descriptionParagraphs.First(IsMemberList));

                memberParagraphCount = descriptionParagraphs.TakeWhile(x => !IsMemberList(x)).Count() + 1;

                return true;
            }

            return false;
        }

        private static bool IsMemberList(IEnumerable<string> x)
        {
            var initialLine = x.FirstOrDefault();

            return initialLine != null
                && (initialLine.StartsWith("  * ename:")
                    || initialLine.StartsWith("  * pname:"));
        }

        private static void GetLines(Dictionary<string, RefIndex> refLookup, Dictionary<string, string[]> fileData, string name, out string summary, out IEnumerable<string> lines)
        {
            var refIndex = refLookup[name];

            var line = fileData[refIndex.FileName][refIndex.LineIndex];

            int nameLength = 12 + name.Length;

            int summaryIndex = line.Skip(nameLength).TakeWhile(x => char.IsPunctuation(x) || char.IsWhiteSpace(x)).Count() + nameLength;

            summary = line.Substring(summaryIndex);
            summary = CapitaliseFirst(summary);

            if (summary.Last() != '.')
            {
                summary += '.';
            }

            lines = fileData[refIndex.FileName].Skip(refIndex.LineIndex + 1).TakeWhile(x => !x.StartsWith("// ref"));
        }

        private static bool IsInclude(IEnumerable<string> paragraph, string keyword)
        {
            return paragraph.Any(paraLine => paraLine.Contains("include::") && paraLine.Contains("../" + keyword));
        }

        private static IEnumerable<string> SplitList(IEnumerable<string> enumerable)
        {
            var elementBuilder = new StringBuilder();

            foreach (var item in enumerable)
            {
                if (item.StartsWith("  *"))
                {
                    if (elementBuilder.Length > 0)
                    {
                        yield return elementBuilder.ToString();

                        elementBuilder.Clear();
                    }

                    elementBuilder.Append(item.Substring(3).Trim());
                }
                else
                {
                    elementBuilder.Append(" " + item.Trim());
                }
            }

            if (elementBuilder.Length > 0)
            {
                yield return elementBuilder.ToString();
            }
        }

        private static string CapitaliseFirst(string value)
        {
            var charArray = value.ToCharArray();

            charArray[0] = char.ToUpper(charArray[0]);

            return new string(charArray);
        }

        private class RefIndex
        {
            public string FileName;
            public int LineIndex;
        }

        private static async Task<string> GetCachedFile(string tempFilePath, string fileUrl)
        {
            var fileUri = new Uri(fileUrl);
            var client = new HttpClient();

            if (!Directory.Exists(tempFilePath))
            {
                Directory.CreateDirectory(tempFilePath);
            }

            string fileName = Path.GetFileName(fileUri.AbsolutePath);

            string tempFile = Path.Combine(tempFilePath, fileName);

            if (!File.Exists(tempFile) || File.GetLastWriteTimeUtc(tempFile) + TimeSpan.FromDays(1) < DateTime.UtcNow)
            {
                Console.WriteLine($"Downloading {tempFile}");

                string fileDirectory = Path.GetDirectoryName(tempFile);

                if (!Directory.Exists(fileDirectory))
                {
                    Directory.CreateDirectory(fileDirectory);
                }

                using (var fileResponse = client.GetAsync(fileUri).Result)
                {
                    if (fileResponse.IsSuccessStatusCode)
                    {
                        using (var tempFileStream = File.OpenWrite(tempFile))
                        {
                            await fileResponse.Content.CopyToAsync(tempFileStream);
                        }
                    }
                }
            }

            return tempFile;
        }
    }

    public static class EnumerableExtensions
    {
        public static IEnumerable<IEnumerable<T>> Split<T>(this IEnumerable<T> enumerable, T seperator, bool removeEmptyEntries = false)
            where T : IEquatable<T>
        {
            var segment = new List<T>();

            foreach (var value in enumerable)
            {
                if (value.Equals(seperator))
                {
                    if (!removeEmptyEntries || segment.Any())
                    {
                        yield return segment.ToArray();

                        segment.Clear();
                    }
                }
                else
                {
                    segment.Add(value);
                }
            }

            if (!removeEmptyEntries || segment.Any())
            {
                yield return segment.ToArray();
            }
        }
    }
}