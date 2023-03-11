using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;

namespace NotionBackupHelpTool
{
    class NotionPage
    {
        public string title;
        public string tag;
        public string id;

        public NotionPage(string _title, string _tag, string _id)
        {
            title = _title;
            tag = _tag;
            id = _id;
        }
    }

    class Config
    {
        public string pageGroupToken;
        public string backupDatabaseToken;
        public string databaseId;

        public Config(string _pageGroupToken, string _backupDatabaseToken, string _databaseId)
        {
            pageGroupToken = _pageGroupToken;
            backupDatabaseToken = _backupDatabaseToken;
            databaseId = _databaseId;
        }
    }

    class Program
    {
        private static List<NotionPage> _notionPageList = new List<NotionPage>();
        private static Dictionary<string, NotionPage> _alreadyInDatabasePageDict = new Dictionary<string, NotionPage>();
        private static List<string> _backupDatabasePages = new List<string>();
        private static string _titlePlaceHolder = "$Title$";
        private static string _tagPlaceHolder = "$Tag$";
        private static string _idPlaceHolder = "$Id$";
        private static string _databaseItemTemplateFileName = "database_item.json";
        private static string _databaseItemStateFileName = "database_item_update_state.json";
        private static string _backupInfoFileName = "backup_list_in_database.json";
        private static string _configFileName = "config.json";

        //token
        //笔记所在分组的token，只读权限即可
        private static string _pageGroupToken = "";
        //备份数据库的token，需要读写权限
        private static string _backupDatabaseToken = "";
        private static string _databaseId = "";
        private static string _backupStateTemplate = "";


        static async Task Main(string[] args)
        {
            if (!InitConfig())
            {
                Console.WriteLine("初始化配置失败，请查看token是否配置正确!");
                Console.Read();
                Environment.Exit(0);
            }
            await GetNotionPageInfo();
            await GetBackupInfoFromDatabase();
            await AddItemsIntoDatabase();
            await GetBackupInfoFromDatabase();
            await UpdateBackupState();
            //写入本地文件
            WriteBackupInfoIntoLocalFile();
            Console.WriteLine("update backup database finished!");
        }

        private static bool InitConfig()
        {
            if (!File.Exists(_configFileName) || !File.Exists(_databaseItemStateFileName))
            {
                return false;
            }

            string jsonConfig = File.ReadAllText(_configFileName);
            Config config = Newtonsoft.Json.JsonConvert.DeserializeObject(jsonConfig, typeof(Config)) as Config;
            if (config != null)
            {
                _pageGroupToken = config.pageGroupToken;
                _backupDatabaseToken = config.backupDatabaseToken;
                _databaseId = config.databaseId;
                _backupStateTemplate = File.ReadAllText(_databaseItemStateFileName); ;
                return true;
            }
            else
            {
                return false;
            }
        }

        #region GetInfo
        public static async Task GetNotionPageInfo()
        {
            var client = new HttpClient();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri("https://api.notion.com/v1/search"),
                Headers =
                {
                    { "accept", "application/json" },
                    { "Notion-Version", "2022-06-28" },
                    { "Authorization",string.Format("Bearer {0}",_pageGroupToken)}
                },
                Content = new StringContent("{\"page_size\":100}")
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };
            using (var response = await client.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                var jsonObj = MiniJSON.Json.Deserialize(body) as Dictionary<string, System.Object>;
                var results = jsonObj["results"] as List<object>;
                ParseNotionPages(results);
            }
        }

        public static void ParseNotionPages(List<object> pages)
        {
            _notionPageList.Clear();
            //目前只处理page类型
            foreach (var item in pages)
            {
                try
                {
                    Dictionary<string, object> itemObj = item as Dictionary<string, object>;
                    if ((itemObj["object"] as string) != "page")
                    {
                        continue;
                    }

                    Dictionary<string, object> properties = itemObj["properties"] as Dictionary<string, object>;

                    Dictionary<string, object> titleProperty = properties["Title"] as Dictionary<string, object>;
                    List<object> titleList = titleProperty["title"] as List<object>;
                    Dictionary<string, object> titleObj = titleList[0] as Dictionary<string, object>;
                    string title = titleObj["plain_text"] as string;
                    title = title.Replace("\n", " ");

                    Dictionary<string, object> tagProperty = properties["Tags"] as Dictionary<string, object>;
                    List<object> tagList = tagProperty["multi_select"] as List<object>;
                    Dictionary<string, object> tagObj = tagList[0] as Dictionary<string, object>;
                    string tag = tagObj["name"] as string;

                    string url = itemObj["url"] as string;
                    string[] splitedUrl = url.Split(new char[] { '-', '/' });
                    string id = splitedUrl[splitedUrl.Length - 1];

                    NotionPage page = new NotionPage(title, tag, id);
                    _notionPageList.Add(page);
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("Exception:{0},stack:{1}", e.Message, e.StackTrace));
                    continue;
                }
            }
        }

        public static void WriteBackupInfoIntoLocalFile()
        {
            //输出到本地文件供校验
            string content1 = Newtonsoft.Json.JsonConvert.SerializeObject(_notionPageList);
            string date = DateTime.Now.ToString("yyyy-MM-dd");
            File.WriteAllText(string.Format("backup_list_{0}.txt", date), content1);

            //记录当前备份数据库的内容
            string content2 = Newtonsoft.Json.JsonConvert.SerializeObject(_alreadyInDatabasePageDict);
            File.WriteAllText(_backupInfoFileName, content2);
        }
        #endregion

        #region AddItemsIntoDatabase

        //拉取现有列表，对比后只把没有的添加到备份数据库
        public static async Task GetBackupInfoFromDatabase()
        {
            //如果本地有之前存的，就用本地的，如果没有则去拉取
            _alreadyInDatabasePageDict.Clear();
            var client = new HttpClient();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri(string.Format("https://api.notion.com/v1/databases/{0}/query", _databaseId)),
                Headers =
                {
                    { "accept", "application/json" },
                    { "Notion-Version", "2022-06-28" },
                    { "Authorization",string.Format("Bearer {0}",_backupDatabaseToken)}
                },
                //body 中的数据，要自己组装
                Content = new StringContent("{\"page_size\":100}")
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };
            using (var response = await client.SendAsync(request))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                var jsonObj = MiniJSON.Json.Deserialize(body) as Dictionary<string, System.Object>;

                var results = jsonObj["results"] as List<object>;
                PaseNotionDatabaseItem(results);
            }
        }

        public static void PaseNotionDatabaseItem(List<object> itemsInDatabase)
        {
            foreach (var item in itemsInDatabase)
            {
                try
                {
                    Dictionary<string, object> itemObj = item as Dictionary<string, object>;
                    if ((itemObj["object"] as string) != "page")
                    {
                        continue;
                    }

                    Dictionary<string, object> properties = itemObj["properties"] as Dictionary<string, object>;

                    Dictionary<string, object> pathProperty = properties["备份位置"] as Dictionary<string, object>;
                    List<object> richTextList = pathProperty["rich_text"] as List<object>;
                    Dictionary<string, object> pathObj = richTextList[0] as Dictionary<string, object>;
                    string path = pathObj["plain_text"] as string;
                    string[] splitedPath = path.Split(new char[] { '/' });
                    int length = splitedPath.Length;
                    string title = splitedPath[length - 1];
                    string tag = splitedPath[length - 2];


                    Dictionary<string, object> idProperty = properties["页面ID"] as Dictionary<string, object>;
                    List<object> idRichTextList = idProperty["rich_text"] as List<object>;
                    Dictionary<string, object> parentIdTextObj = idRichTextList[0] as Dictionary<string, object>;
                    var idTextObj = parentIdTextObj["text"] as Dictionary<string, object>;
                    string id = idTextObj["content"] as string;

                    NotionPage page = new NotionPage(path, tag, id);
                    if (!_alreadyInDatabasePageDict.ContainsKey(id))
                    {
                        _alreadyInDatabasePageDict.Add(id, page);
                    }

                    string url = itemObj["url"] as string;
                    string[] splitedUrl = url.Split(new char[] { '-', '/' });
                    string childPageId = splitedUrl[splitedUrl.Length - 1];
                    if (!_backupDatabasePages.Contains(childPageId))
                    {
                        _backupDatabasePages.Add(childPageId);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(string.Format("Exception:{0},stack:{1}", e.Message, e.StackTrace));
                    continue;
                }
            }
        }

        private static async Task AddItemsIntoDatabase()
        {
            foreach (var item in _notionPageList)
            {
                if (IsItemAlreadyInDatabase(item.id))
                {
                    continue;
                }

                _alreadyInDatabasePageDict.Add(item.id, item);
                await InsertItemIntoDatabase(item);
            }
        }

        public static async Task InsertItemIntoDatabase(NotionPage page)
        {
            string template = string.Empty;
            if (File.Exists(_databaseItemTemplateFileName))
            {
                template = File.ReadAllText(_databaseItemTemplateFileName);
            }

            template = template.Replace(_titlePlaceHolder, page.title);
            template = template.Replace(_tagPlaceHolder, page.tag);
            template = template.Replace(_idPlaceHolder, page.id);

            var client = new HttpClient();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Post,
                RequestUri = new Uri("https://api.notion.com/v1/pages"),
                Headers =
                {
                    { "accept", "application/json" },
                    { "Notion-Version", "2022-06-28" },
                    { "Authorization",string.Format("Bearer {0}",_backupDatabaseToken)}
                },
                //body 中的数据，要自己组装
                Content = new StringContent(template)
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };
            using (var response = await client.SendAsync(request))
            {
                //response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine("");
                Console.WriteLine(string.Format("create item <{0}> ", body));
            }
        }

        private static bool IsItemAlreadyInDatabase(string id)
        {
            if (_alreadyInDatabasePageDict.ContainsKey(id))
            {
                return true;
            }
            return false;
        }
        #endregion

        #region UpdateBackupState
        private static async Task UpdateBackupState()
        {
            foreach (var item in _backupDatabasePages)
            {
                await UpdateItemBackupState(item);
            }
        }

        private static async Task UpdateItemBackupState(string id)
        {
            var client = new HttpClient();
            var request = new HttpRequestMessage
            {
                Method = HttpMethod.Patch,
                RequestUri = new Uri(string.Format("https://api.notion.com/v1/pages/{0}", id)),
                Headers =
                {
                    { "accept", "application/json" },
                    { "Notion-Version", "2022-06-28" },
                    { "Authorization",string.Format("Bearer {0}",_backupDatabaseToken)}
                },
                Content = new StringContent(_backupStateTemplate)
                {
                    Headers =
                    {
                        ContentType = new MediaTypeHeaderValue("application/json")
                    }
                }
            };
            using (var response = await client.SendAsync(request))
            {
                var body = await response.Content.ReadAsStringAsync();
                Console.WriteLine("");
                Console.WriteLine(body);
            }
        }
        #endregion
    }
}
