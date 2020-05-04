
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace FunPlus.AssetManage
{
    using Resources = UnityEngine.Resources;


    public class AbLoader
    {
        public static bool OUTPUT_LOG =
#if UNITY_EDITOR
        true;
#else
        false;
#endif //UNITY_EDITOR


        private class AbInfo
        {
            //assetBundle
            public AssetBundle ab;
            //加载时，依赖的内容
            public AssetRefList refList;
            //最近使用的帧数，超过一定帧后才触发删除
            public int usedFrame;
            //没用引用后，超过一定帧后才触发删除
            public int unusedFrame;
            //io时用的加密流
            public Stream stream;
        }

        public string rootPath { get; private set; }

        public string LoaderName { get; private set; }

        private AssetBundleManifest manifest;
        private Stream manifestStream = null;
        private Dictionary<string, AbInfo> loadedAbs = new Dictionary<string, AbInfo>();

        // <asetPath, bundleName>
        private AssetBundleMap bundleMap = new AssetBundleMap();

        //异步加载时使用的ab，这时刚刚加载的ab，保证不能释放掉
        private HashSet<string> usingAbs = new HashSet<string>();

        //缓存依赖查询，减少gc
        private Dictionary<string, HashSet<string>> cachedDeps = new Dictionary<string, HashSet<string>>();

        //缓存所有的ab名，用来判断ab包是否存在
        //private HashSet<string> allAbNames = new HashSet<string>();

        private AssetRefManager abRefMgr;

        //无引用时的缓存
        private LRUCache lruCache;
        private int cacheSize;

        //自动卸载一帧一次最多卸载的数量
        public int autoUnloadMaxCount = 5;
        //自动卸载一帧消耗最久用时（毫秒）
        public float autoUnloadMaxMilliSecUse = 3.0f;
        //自动卸载，完整卸载完成后，下次卸载间隔
        public float autoUnloadFullUnloadInterval = 5.0f;
        
        //检查某个ab是否需要缓存
        public CheckCache checkCache;
        private BundleStreamCreator createEncyptStream;
        private bool released = false;

        public bool Init(string path, int lruSize,BundleStreamCreator encypt_)
        {

            //Debug.LogError("====Init AbLoader " + root + "  " + (encypt_ != null));

            released = false;
            createEncyptStream = encypt_;
            manifest = null;
            loadedAbs.Clear();
            usingAbs.Clear();
            this.cacheSize = lruSize;
            lruCache = new LRUCache(lruSize);
            //allAbNames.Clear();
            abRefMgr = new AssetRefManager();
            this.rootPath = path;


            AssetBundleMap.LoadTxt(rootPath, bundleMap);


            // 根目录的ab没加密
            AssetBundle ab = AssetBundle.LoadFromFile(GetFullPath(Path.GetFileName(rootPath)));


            if (ab == null)
            {
                Debug.LogError("Load Manifest failed." + rootPath);
                return false;
            }
            manifest = ab.LoadAsset<AssetBundleManifest>("AssetBundleManifest");
            if (manifest == null)
            {
                ab.Unload(true);
                Debug.LogError("Load Manifest failed.");
                return false;
            }



            return true;
        }

        public void Release()
        {
            released = true;
            usingAbs.Clear();
            abRefMgr.Clear();
            UnloadUnusedTotal();
            if (manifest != null)
            {
                Resources.UnloadAsset(manifest);
            }
            manifest = null;
            loadedAbs.Clear();

            if (manifestStream != null)
            {
                manifestStream.Close();
                manifestStream = null;
            }
        }

        public AssetHandle LoadAssetBundle(string abPath)
        {
            var ab = LoadAb(abPath);
            if (ab == null)
            {
                return AssetHandle.invalid;
            }
            var abRef = abRefMgr.GetOrCreateRef(ab);
            return new AssetHandle(ab, abRef);
        }

        public AssetHandle LoadFromAb(string abPath, string assetName, Type type)
        {
            //从ab包重加载资源，返回资源，和一个引用
            AssetBundle ab = LoadAb(abPath);
            if (ab == null)
            {
                return AssetHandle.invalid;
            }
            Object asset = type == null ? ab.LoadAsset(assetName) : ab.LoadAsset(assetName, type);
            if (asset != null)
            {
                //加载成功
                var assetRef = abRefMgr.GetOrCreateRef(ab);
                return new AssetHandle(asset, assetRef);
            }
            return AssetHandle.invalid;
        }

        public AssetHandle LoadFromAb<T>(string abPath, string assetName) where T : Object
        {
            AssetBundle ab = LoadAb(abPath);
            if (ab == null)
            {
                return AssetHandle.invalid;
            }
            T asset = ab.LoadAsset<T>(assetName);
            if (asset != null)
            {
                //加载成功
                var assetRef = abRefMgr.GetOrCreateRef(ab);
                return new AssetHandle(asset, assetRef);
            }
            return AssetHandle.invalid;
        }

        public bool LoadFromAbAsync(string abPath, string assetName, AssetLoadRequest req)
        {
            AssetBundle ab = LoadAb(abPath);
            if (ab == null)
            {
                Debug.LogError("load ab failed." + abPath);
                return false;
            }


            var loadReq = req.type == null ? ab.LoadAssetAsync(assetName)
                : ab.LoadAssetAsync(assetName, req.type);
            if (loadReq == null)
            {
                Debug.LogError("load from ab failed." + abPath + "," + assetName);
                return false;
            }
            usingAbs.Add(abPath);
            //assetbundle优先级是越大越先加载
            loadReq.priority = AssetLoadRequest.Priority_Max - req.priority;
            loadReq.completed += (opt) =>
            {
                if (req != null)
                {
                    var assetLoadReq = opt as AssetBundleRequest;
                    if (assetLoadReq != null)
                    {
                        var asset = assetLoadReq.asset;
                        if (asset != null)
                        {
                            var handle = new AssetHandle(asset, abRefMgr.GetOrCreateRef(ab));
                            req.assetHandle = handle;
                        }

                        if (asset == null)
                        {
                            if ( OUTPUT_LOG)
                            {
                                UnityEngine.Debug.LogWarning("load failed:" + abPath + "/" + assetName);
                                if (!ab.Contains(assetName))
                                {
                                    UnityEngine.Debug.LogError("ab not contains asset." + assetName);
                                }
                            }
                        }
                    }

                    req.Complete();
                }
                usingAbs.Remove(abPath);
            };
            return true;
        }

        private HashSet<string> GetDirectDependencies(string abPath)
        {
            HashSet<string> depSet;
            if (cachedDeps.TryGetValue(abPath, out depSet))
            {
                return depSet;
            }
            string[] deps = manifest.GetDirectDependencies(abPath);
            depSet = new HashSet<string>(deps);
            depSet.Remove(abPath);      //防止自己引用自己
            cachedDeps[abPath] = depSet;
            return depSet;
        }

        //加载一个ab包，并且加载他的依赖
        private AssetBundle LoadAb(string abPath)
        {
            AbInfo info = LoadAbStep(abPath);
            if (info != null)
            {
                return info.ab;
            }
            return null;
        }

        private AbInfo LoadAbStep(string abPath, int level = 0)
        {
            bool isLoaded = false;
            AbInfo inf = _LoadAb(abPath, level, out isLoaded);
            if (inf == null)
            {
                return null;
            }
            if (isLoaded)
            {
                return inf;
            }

            var deps = GetDirectDependencies(abPath);

            foreach (string dep in deps)
            {
                AbInfo depAb = LoadAbStep(dep, level+1);
                if (depAb == null)
                {
                    continue;
                }
                if (inf.refList == null) inf.refList = new AssetRefList();
                //引用这个ab
                inf.refList.AddRef(abRefMgr.GetOrCreateRef(depAb.ab));
            }

            return inf;
        }

        private bool _UnloadAb(string abPath)
        {
            AbInfo info;

            if (!loadedAbs.TryGetValue(abPath, out info))
            {
                return false;
            }

            //正在异步加载的，不可以被卸载
            if (usingAbs.Contains(abPath))
            {
                return false;
            }

            //销毁ab引用
            abRefMgr.DestroyRef(info.ab);
            //释放所有对依赖的引用
            if (info.refList != null)
            {
                info.refList.ClearRef();
            }
            loadedAbs.Remove(abPath);
            info.ab.Unload(true);
            if (info.stream != null)
            {
                info.stream.Dispose();
            }

            if (!released)
            {
                if ( OUTPUT_LOG )
                {
                    Debug.Log("<color='red'>[unload ab:" + abPath + "," + Time.frameCount + "</color>");
                }
            }

            //这里不做不立即释放依赖，依赖等下次自动释放

            return true;
        }

        public bool HasAnyRef(string abPath)
        {
            AbInfo ab;
            if (!loadedAbs.TryGetValue(abPath, out ab))
            {
                return false;
            }
            return HasAnyRef(ab);
        }

        private bool HasAnyRef(AbInfo info)
        {
            //这个ab还有依赖引用，不能释放
            if (abRefMgr.HasRef(info.ab))
            {
                return true;
            }

            return false;
        }

        //加载一个Ab，上层处理引用
        private AbInfo _LoadAb(string abPath, int level , out bool isLoaded)
        {
            isLoaded = false;

#if UNITY_EDITOR
            OnStartLoad(abPath);
#endif

            AbInfo info;
            if (loadedAbs.TryGetValue(abPath, out info))
            {
                info.usedFrame = Time.frameCount;
                info.unusedFrame = 0;
                isLoaded = true;
#if UNITY_EDITOR
                TestIfHitCache(abPath);
#endif
                return info;
            }

            // if (!allAbNames.Contains(abPath))
            // {
            //     return null;
            // }

            string fullPath = GetFullPath(abPath);
            AssetBundle ab = null;
            Stream stream = null;

            if (createEncyptStream != null)
            {
                stream = createEncyptStream(fullPath);
                ab = AssetBundle.LoadFromStream(stream);
            }
            else
            {
                ab = AssetBundle.LoadFromFile(fullPath);
            }


            if (level == 0)
            {
                if (OUTPUT_LOG)
                {
                    Debug.Log("<color='green'>Load Ab:" + abPath + ".</color>");
                }
            }
            else
            {
                if ( OUTPUT_LOG )
                {
                    string levelStr = new string('-', level * 4);
                    Debug.LogFormat("<color='blue'>-{0}>Load dep Ab:{1}.</color>", levelStr, abPath);
                }
            }

            if (ab == null)
            {
                Debug.LogError("LoadAssetBundle faild " + fullPath);
                return null;
            }

            AbInfo inf = new AbInfo();
            inf.ab = ab;
            inf.usedFrame = Time.frameCount;
            inf.stream = stream;

            loadedAbs.Add(abPath, inf);
            return inf;
        }

        private string GetFullPath(string abPath)
        {
            //TODO,增加路径缓存
            return Path.Combine(rootPath, abPath);
        }

        //释放一步
        //返回true表示释放完
        public bool UnloadUnusedStep()
        {
            tmps.Clear();
            foreach(var pair in loadedAbs)
            {
                var info = pair.Value;
                if (!HasAnyRef(info))
                {
                    tmps.Add(pair.Key);
                }
            }
            bool cleard = tmps.Count == 0;
            foreach(var key in tmps)
            {
                _UnloadAb(key);
            }
            return cleard;
        }

        //完全释放
        public void UnloadUnusedTotal()
        {
            while (!UnloadUnusedStep())
            {
                ;;
            }
        }

        private List<string> tmps = new List<string>();
        private Stopwatch stopwatch = new Stopwatch();
        //卸载无引用ab
        public bool UnloadUnused(int maxUnload = -1, float autoBreakTime = -1f)
        {
            tmps.Clear();
            int currFameCount = Time.frameCount;
            int shouldUnloadCount = 0;
            foreach (var pair in loadedAbs)
            {
                AbInfo info = pair.Value;
                if (currFameCount - info.usedFrame < 30) //30帧之内，不检查引用计数，不做清理
                {
                    continue;
                }
                if (!HasAnyRef(info))
                {
                    bool shouldCache = checkCache != null ? checkCache(pair.Key) : true;

                    if (shouldCache)
                    {
                        if (info.unusedFrame == 0)
                        {
                            info.unusedFrame = currFameCount;
                            lruCache.Put(pair.Key);
                        }
                        if (currFameCount - info.unusedFrame < 30)   //无引用缓存至少30帧
                        {
                            continue;
                        }
                    }

                    tmps.Add(pair.Key);
                    //有最大卸载数，先收集
                    if (maxUnload > 0)
                    {
                        ++shouldUnloadCount;
                        if (shouldUnloadCount >= maxUnload * 2)
                        {
                            break;
                        }
                    }
                }
            }

            int count = 0;
            stopwatch.Reset();
            bool unloadAll = true;
            foreach (var tmp in tmps)
            {
                if (lruCache.Contains(tmp))
                {
                    continue;
                }
                stopwatch.Start();
                if (!_UnloadAb(tmp))
                {
                    //没发生卸载
                    continue;
                }
                count++;
                stopwatch.Stop();
                if (maxUnload > 0 && count >= maxUnload)
                {
                    unloadAll = false;
                    break;
                }
                if (autoBreakTime > 0 && stopwatch.ElapsedMilliseconds > autoBreakTime)
                {
                    unloadAll = false;
                    break;
                }
            }
            tmps.Clear();
            return unloadAll;
        }

        public IEnumerator AutoClear()
        {
            while (true)
            {
                //一帧最多卸载5个，超过3毫秒就停止，等到下一帧继续卸载
                bool unloadAll = UnloadUnused(autoUnloadMaxCount, autoUnloadMaxMilliSecUse);
                if (unloadAll)
                {
                    //卸载完了，等5s后再触发
                    yield return new WaitForSeconds(autoUnloadFullUnloadInterval);
                }
                else
                {
                    //分帧了，下帧继续
                    yield return null;
                }
            }
        }

        public bool ContainAsset(string assetPath)
        {
            return !string.IsNullOrEmpty(bundleMap.GetBundle(assetPath));
        }

        public bool GetAbPath(string assetPath,out string abPath,out string assetName)
        {
            return bundleMap.GetAbPath(assetPath,out abPath,out assetName);
        }

        //编辑器接口
#if UNITY_EDITOR
        int hitCacheCount = 0;
        int loadCount = 0;
        private void TestIfHitCache(string abPath)
        {
            if (lruCache.Contains(abPath))
            {
                //Debug.Log("hit cache:" + abPath);
                hitCacheCount++;
            }
        }
        private void OnStartLoad(string abPath)
        {
            loadCount++;
        }

        public class AbEditorInfo
        {
            public UnityEngine.Object ab;
            public AssetRefList deps;
            public string abPath;
        }

        //加载的ab
        public List<AbEditorInfo> GetLoadedAbs()
        {
            List<AbEditorInfo> objs = new List<AbEditorInfo>();
            foreach(var pair in loadedAbs)
            {
                objs.Add(new AbEditorInfo()
                {
                    ab = pair.Value.ab,
                    deps = pair.Value.refList,
                    abPath = pair.Key,
                });
            }
            return objs;
        }

        //缓存内容
        public string[] GetLruInfo()
        {
            return lruCache.ToArray();
        }

        //缓存命中率
        public float cacheHitPercent { get { return hitCacheCount / (float)loadCount; }}
#endif
    }
}
