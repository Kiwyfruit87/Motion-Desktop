using System;
using System.IO;
using System.Net;
using System.Text;

namespace VideoWallpaper
{
    // 網路查詢（天氣、歌詞共用）。TLS 1.2 在程式啟動時就開好了（見 Program）
    static class Web
    {
        // 下載文字。missingIsNull = true 時，查不到（404 / 400）回傳 null；連不上之類的錯誤照樣丟出去
        public static string Get(string url, bool missingIsNull = false)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.UserAgent = "VideoWallpaper/1.0";
            request.Timeout = 8000;
            try
            {
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                // 錯誤的回應也要關掉，不然連線一直佔著（同一個網站預設只開 2 條），之後的查詢會卡到逾時
                using (var http = ex.Response as HttpWebResponse)
                    if (missingIsNull && http != null && ((int)http.StatusCode == 404 || (int)http.StatusCode == 400)) return null;
                throw;
            }
        }

        // JSON → 物件：{...} 是 Dictionary<string, object>、[...] 是 object[]
        public static object Json(string text)
        {
            return new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(text);
        }
    }
}
