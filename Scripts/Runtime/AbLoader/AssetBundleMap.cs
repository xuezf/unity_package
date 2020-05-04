using System.Collections.Generic;
using System.IO;

using UnityEngine;


namespace FunPlus.AssetManage
{
    public class AssetBundleMap
    {
        public static readonly string FILE_NAME = "bundle_map";

        public static bool forceLower = true;

        private struct AssetPathInfo
        {
            public string abPath;
            public string ext;
        }

        //<assetName, bundlePath>   <"Asset/Art/UI/panel01", "AssetBundle/UI/PanelBundle.bundle">
        //private Dictionary<string, string> assetBundleMap = new Dictionary<string, string>();
        //<assetPath, ext>  <"Asset/Art/UI/panel01", ".prefab">
        //private Dictionary<string, string> assetExtMap = new Dictionary<string, string>();

        private Dictionary<string, AssetPathInfo> assetMap = new Dictionary<string, AssetPathInfo>();

        public bool GetAbPath(string assetPath, out string abPath, out string assetName)
        {
            string path = forceLower ? assetPath.ToLower() : assetPath;
            if (!assetMap.TryGetValue(path, out var info))
            {
                abPath = null;
                assetName = null;
                return false;
            }

            abPath = info.abPath;
            assetName = path + info.ext;
            return true;
        }

        public string GetBundle(string str)
        {
            string path = forceLower ? str.ToLower() : str;
            if (!assetMap.TryGetValue(path, out var info))
            {
                return null;
            }
            return info.abPath;
        }


        public static bool LoadTxt(string root, AssetBundleMap abMap)
        {
            if (forceLower)
            {
                Debug.LogError("====AssetBundleMap forceLower");
            }

            string path = Path.Combine(root, FILE_NAME + ".txt");

            using (FileStream fs = File.Open(path, FileMode.Open))
            {
                if (fs != null)
                {
                    using (StreamReader sr = new StreamReader(fs))
                    {
                        string line = sr.ReadLine();
                        while (line != null)
                        {
                            string[] strs = line.Split(':');
                            if (strs == null || strs.Length != 2)
                            {
                                Debug.LogError(string.Format("===={0} readLine Error {1}", path, line));
                            }

                            string assetFullPath = forceLower ? strs[0].ToLower() : strs[0];
                            string assetName = assetFullPath.Substring(0, assetFullPath.LastIndexOf('.')).ToLower();
                            string ext = Path.GetExtension(assetFullPath);
                            string bundlePath = strs[1];

                            abMap.assetMap[assetName] = new AssetPathInfo()
                            {
                                abPath = bundlePath,
                                ext = ext,
                            };

                            line = sr.ReadLine();
                        }
                    }
                }
            }

            return true;
        }

        public static bool SaveTxt(string root, Dictionary<string, string> bundleMap)
        {

            string filePath = Path.Combine(root, FILE_NAME + ".txt");
            using (FileStream fs = File.Open(filePath, FileMode.Create))
            {
                using (StreamWriter sw = new StreamWriter(fs))
                {

                    for (var e = bundleMap.GetEnumerator(); e.MoveNext();)
                    {
                        string assetName = e.Current.Key.Replace('\\', '/');
                        string bundleName = e.Current.Value.Replace('\\', '/');

                        //assetName = assetName.Substring(0, assetName.LastIndexOf('.'));

                        sw.WriteLine(string.Format("{0}:{1}", assetName, bundleName));
                    }

                    sw.Flush();
                }
            }

            return true;
        }
    }
}
