using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using static System.Runtime.InteropServices.JavaScript.JSType;

namespace small_window2
{

    internal static class MainContext
    {
        private const string FilePath = ".\\config.ini";
        public static byte Alpha = 80;

        /// <summary>把当前 Alpha 写入磁盘（格式：alpha=127）</summary>
        public static void Save() {
            try
            {
                File.WriteAllText(FilePath, $"alpha={Alpha}", Encoding.UTF8);
            }
            catch (Exception)
            {

                MessageBox.Show("保存时无法写入，请检查 config.ini 是否被占用");
            }
            
        }
        static MainContext() {
            Load();
        }
        /// <summary>从磁盘读取 Alpha；若文件不存在或格式非法则保持默认值</summary>
        public static void Load()
        {
            if (!File.Exists(FilePath)) return;

            var text = File.ReadAllText(FilePath, Encoding.UTF8).Trim();

            // 允许大小写并去掉前缀再解析
            const string prefix = "alpha=";
            //从 prefix.Length 开始解析 ，AsSpan 是为了避免不必要的字符串分配
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                byte.TryParse(text.AsSpan(prefix.Length), out var value))
            {
                if (value<200&& value>30)
                {
                    Alpha = value;
                }
            }
        }
    }
}
