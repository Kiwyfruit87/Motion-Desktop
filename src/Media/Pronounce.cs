using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;

namespace VideoWallpaper
{
    // 英文字怎麼念（逐字亮估算每個字唱多久用）：CMU 發音字典（data/cmudict.gz，授權見 data/cmudict-LICENSE.txt）事先轉成的精簡表，
    // 編進 exe 裡。每個字記每個音節的份量（重音、長母音唱得久，輕讀的 schwa 很短）和子音有幾個。
    // 第一次用到時在背景讀進來（約 12 萬字），讀好之前查不到（照原本的拼字規則估）
    static class Pronounce
    {
        static volatile Dictionary<string, string> table;
        static int loading;

        // 先在背景讀進來（打開歌詞時呼叫，放大歌詞時通常已經好了）
        public static void Preload()
        {
            if (table != null || Interlocked.Exchange(ref loading, 1) == 1) return;
            var t = new Thread(Load) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Pronounce" };
            t.Start();
        }

        public static bool Ready { get { return table != null; } }

        static void Load()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("cmudict"))
                {
                    if (s == null) return;
                    using (var gz = new GZipStream(s, CompressionMode.Decompress))
                    using (var r = new StreamReader(gz, Encoding.UTF8))
                    {
                        var d = new Dictionary<string, string>(130000, StringComparer.Ordinal);
                        string line;
                        while ((line = r.ReadLine()) != null)
                        {
                            int tab = line.IndexOf('\t');
                            if (tab > 0) d[line.Substring(0, tab)] = line.Substring(tab + 1);
                        }
                        table = d;
                    }
                }
            }
            catch { }
        }

        // 這個字每個音節的份量（1 大約是一般的重音音節）和子音有幾個；查不到（還沒讀好、不是英文、字典沒有）回傳 null
        public static double[] Syllables(string word, out int consonants)
        {
            consonants = 0;
            var d = table;
            if (d == null) { Preload(); return null; }
            string key = word.ToLowerInvariant().Replace("'", "").Replace("’", "");
            if (key.Length == 0) return null;
            string code;
            if (!d.TryGetValue(key, out code))
            {
                // holdin'、nothin'：唱的時候省掉 g，字典裡是 holding
                bool dropped = key.EndsWith("in") && (word.EndsWith("'") || word.EndsWith("’"));
                if (!dropped || !d.TryGetValue(key + "g", out code)) return null;
            }
            int bar = code.IndexOf('|');
            if (bar <= 0) return null;
            var syl = new double[bar];
            for (int i = 0; i < bar; i++) syl[i] = 0.5 + (code[i] - 'a') / 20.0;
            int.TryParse(code.Substring(bar + 1), out consonants);
            return syl;
        }
    }
}
