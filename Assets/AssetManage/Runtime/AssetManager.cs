using System.Linq;
using System.IO;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine.SceneManagement;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace FunPlus.AssetManage
{
    public class AssetManager : MonoBehaviour
    {
#region  load handle
        private interface LoadHandle
        {
            AssetHandle Load(Type type = null);
            AssetHandle Load<T>() where T : UnityEngine.Object;
            bool LoadAsync(AssetLoadRequest req);
            IEnumerator LoadScene(LoadSceneMode mode);
        }

        private struct InvalidLoadHandle : LoadHandle
        {
            public AssetHandle Load(Type type = null)
            {
                return AssetHandle.invalid;
            }

            public AssetHandle Load<T>() where T : UnityEngine.Object
            {
                return AssetHandle.invalid;
            }

            public bool LoadAsync(AssetLoadRequest req)
            {
                req.Complete();
                return false;
            }

            public IEnumerator LoadScene(LoadSceneMode mode)
            {
                yield break;
            }
        }

        private struct AbHandle : LoadHandle
        {
            public AbLoader abLoader;
            public string abPath;
            public string assetName;

            public AssetHandle Load(Type type = null)
            {
                return abLoader.LoadFromAb(abPath, assetName, type);
            }

            public AssetHandle Load<T>() where T : UnityEngine.Object
            {
                return abLoader.LoadFromAb<T>(abPath, assetName);
            }

            public bool LoadAsync(AssetLoadRequest req)
            {
                return abLoader.LoadFromAbAsync(abPath, assetName, req);
            }

            public IEnumerator LoadScene(LoadSceneMode mode)
            {
                var assetHandle = abLoader.LoadAssetBundle(abPath);
                var ab = assetHandle.asset as AssetBundle;
                if (ab == null)
                {
                    UnityEngine.Debug.LogError("load scene failed." + assetName);
                    yield break;
                }
                AssetManager.GetInstance().WeakCache(assetHandle);
                var loadScenePath = ab.GetAllScenePaths()[0];
                yield return SceneManager.LoadSceneAsync(loadScenePath, mode);
            }
        }

        private struct WebLoadHandle : LoadHandle
        {
            private WebLoader loader;

            public AssetHandle Load(Type type = null)
            {
                throw new NotImplementedException();
            }

            public AssetHandle Load<T>() where T : UnityEngine.Object
            {
                throw new NotImplementedException();
            }

            public bool LoadAsync(AssetLoadRequest req)
            {
                loader.Load(req);
                return true;
            }

            public IEnumerator LoadScene(LoadSceneMode mode)
            {
                throw new NotImplementedException();
            }
        }

#if UNITY_EDITOR
        private struct EditorLoadHandle : LoadHandle
        {
            public string assetPath;

            public AssetHandle Load(Type type = null)
            {
                if (type == typeof(Sprite))
                {
                    type = null;
                }
                return GetInstance().LoadAssetAtEditor(assetPath,type);
            }

            public AssetHandle Load<T>() where T : UnityEngine.Object
            {
                return GetInstance().LoadAssetAtEditor(assetPath,typeof(T));
            }

            public bool LoadAsync(AssetLoadRequest req)
            {
                req.assetHandle = Load(req.type);
                req.Complete();
                return req.assetHandle.isValid;
            }

            public IEnumerator LoadScene(LoadSceneMode mode)
            {
                yield return UnityEditor.SceneManagement.EditorSceneManager.LoadSceneAsync(assetPath,LoadSceneMode.Single);
            }
        }
#endif
#endregion

        private PriorityQueue<AssetLoadRequest> requestQueue = new PriorityQueue<AssetLoadRequest>();
        private Coroutine loadProcessCoroutine;

        //资源关联的引用，key是asset的instanceId,value是关联的引用
        //如果是ab加载的资源，则引用关联ab包，而不是asset本身
        //如果是Resouce、Web加载的资源，则引用关联asset本身
        private Dictionary<int, AssetRef> assetRefDict = new Dictionary<int, AssetRef>();

        private List<AbLoader> abLoaders = new List<AbLoader>();

        private AssetRefList weakRefs = new AssetRefList();
        private AssetRefList strongRefs = new AssetRefList();

        public bool isEditorMode = false;

        private Dictionary<string, LoadHandle> loadHandleCache = new Dictionary<string, LoadHandle>();

        private Dictionary<string,WebLoader> webLoaders = new Dictionary<string, WebLoader>();

        private static AssetManager ins;

        public int maxLoadCountPerFrame {get; set;} = 4;

        public static AssetManager GetInstance()
        {
            return ins;
        }

        public static void Init()
        {
            var last = GameObject.FindObjectOfType<AssetManager>();
            if (last != null)
            {
                DestroyImmediate(last.gameObject);
            }
            GameObject go = new GameObject("AssetManager");
            go.AddComponent<AssetManager>();
            GameObject.DontDestroyOnLoad(go);
        }

        public static void Release()
        {
            if (ins != null)
            {
                DestroyImmediate(ins.gameObject);
            }
        }

        private void Awake()
        {
            ins = this;
        }

        private void OnDestroy()
        {
            ins = null;
            loadHandleCache.Clear();
            foreach (var abLoader in abLoaders)
            {
                abLoader.Release();
            }
            abLoaders.Clear();
            #if UNITY_EDITOR
            editorAssetRefMagr.Clear();
            #endif
        }


        public void AddBundle(string bundlePath,AbCacheConf cacheConf,BundleStreamCreator encypt = null)
        {
            if (abLoaders.FindIndex((loader) => loader.rootPath == bundlePath) != -1)
            {
                Debug.LogError("already has bundle root:" + bundlePath);
                return;
            }
            var abLoader = new AbLoader();
            abLoader.Init(bundlePath,cacheConf.cacheSize,encypt);
            abLoader.checkCache = cacheConf.checkCacheMethod;
            abLoaders.Add(abLoader);
            StartCoroutine(abLoader.AutoClear());
        }

        public AssetLoadRequest LoadAssetAsync<T>(string path
            , AssetLoadRequest.OnCompleted cb = null
            , int priority = AssetLoadRequest.Priority_Common) where T : UnityEngine.Object
        {
            return LoadAssetAsync(path, cb, typeof(T), priority);
        }

        public AssetLoadRequest LoadAssetAsync(string path
                , AssetLoadRequest.OnCompleted cb = null
                , Type type = null
                , int priority = AssetLoadRequest.Priority_Common)
        {
            return LoadAsync(path, null, cb, type, priority);
        }

        public AssetLoadRequest LoadAssetAsync<T>(string path
            , GameObject autoRefGameObject
            , AssetLoadRequest.OnCompleted cb = null
            , int priority = AssetLoadRequest.Priority_Common
            ) where T : UnityEngine.Object
        {
            return LoadAsync(path, autoRefGameObject, cb, typeof(T), priority);
        }

        public AssetLoadRequest LoadAsync(string path
            , GameObject autoRefGameObject
            , AssetLoadRequest.OnCompleted cb = null
            , Type type = null
            , int priority = AssetLoadRequest.Priority_Common
            )
        {
            var req = AssetLoadRequest.Get();
            req.path = path;
            req.type = type;
            req.onCompleted = cb;
            req.autoRefGameObject = autoRefGameObject;
            req.priority = priority;
            req.autoFree = true;
            StartLoad(req);
            return req;
        }

        //同步加载资源
        //如果autoRefGameObject为空，注意需要手动引用，否则资源会被卸载掉
        public AssetHandle LoadAsset<T>(string path,GameObject autoRefGameObject = null)
        {
            return LoadAsset(path,typeof(T),autoRefGameObject);
        }

        //同步加载资源
        //如果autoRefGameObject为空，注意需要手动引用，否则资源会被卸载掉
        public AssetHandle LoadAsset(string path, Type type = null, GameObject autoRefGameObject = null)
        {
            var abHandle = GetLoadHandle(path);
            var assetHandle = abHandle.Load(type);
            if (assetHandle.isValid)
            {
                return assetHandle;
            }
            OnLoadAssetSucc(assetHandle);
            if (autoRefGameObject != null)
            {
                AddAssetRefToGameObject(gameObject, assetHandle.assetRef);
            }
            return assetHandle;
        }

        //加载并实例化一个GameObject
        public GameObject LoadGameObject(string path)
        {
            var assetHandle = LoadAsset<GameObject>(path);
            if (!assetHandle.isValid)
            {
                return null;
            }
            return InstantiateSafe(assetHandle);
        }

        private GameObject InstantiateSafe(AssetHandle assetHandle)
        {
            var goAsset = assetHandle.asset as GameObject;
            if (goAsset == null)
            {
                return null;
            }
            var go = GameObject.Instantiate<GameObject>(goAsset);
            if (go == null)
            {
                return null;
            }
            var helper = go.AddComponent<AssetRefHelper>();
            helper.RefAsset(assetHandle.assetRef);
            return go;
        }

        private GameObject InstantiateSafe(GameObject asset)
        {
            if (asset == null)
            {
                return null;
            }
            var assetRef = GetAssetRef(asset);
            if (assetRef == null)
            {
                return null;
            }
            var go = GameObject.Instantiate<GameObject>(asset);
            if (go == null)
            {
                return null;
            }
            var helper = go.AddComponent<AssetRefHelper>();
            helper.RefAsset(assetRef);
            return go;
        }

        //实例化Prefab，并且关联资源引用到实例化到GameObject
        //GameObject销毁时，释放引用
        public static new GameObject Instantiate(UnityEngine.Object asset)
        {
            return GetInstance().InstantiateSafe(asset as GameObject);
        }

        //手动加载一个ab，慎用
        //需要自己管理好引用计数
        public AssetHandle LoadAssetBundle(string fullAbPath)
        {
            var loader = abLoaders.Find((abLoader)=> {return fullAbPath.StartsWith(abLoader.rootPath); });
            if (loader == null)
            {
                return AssetHandle.invalid;
            }
            int index = fullAbPath.IndexOf(loader.rootPath);
            string abPath = fullAbPath.Remove(index,loader.rootPath.Length).TrimStart('/','\\');
            return loader.LoadAssetBundle(abPath);
        }

        //预加载一个ab
        //会自动弱引用
        public AssetHandle PreloadAssetBundle(string fullAbPath)
        {
            var assetHandle = LoadAssetBundle(fullAbPath);
            if (assetHandle.isValid)
            {
                WeakCache(assetHandle);
            }
            return assetHandle;
        }

        //加载并且缓存一个ab
        //适用于不卸载的资源
        public AssetHandle CacheAssetBundle(string fullAbPath)
        {
            var assetHandle = LoadAssetBundle(fullAbPath);
            if (assetHandle.isValid)
            {
                CacheAsset(assetHandle);
            }
            return assetHandle;
        }        

        public void StartLoad(AssetLoadRequest req)
        {
            if (req == null)
            {
                return;
            }
            if (req.priority <= AssetLoadRequest.Priority_Sync)
            {
                DoLoadSync(req);
                return;
            }
            if (loadProcessCoroutine == null)
            {
                loadProcessCoroutine = StartCoroutine(AssetLoadProcess());
                StartCoroutine(AutoClearInvalidAssetRef());
            }
            requestQueue.Enqueue(req.priority, req);
        }

        //释放无用资源
        //内存紧张需要释放干净，传入true
        //默认不会释放干净，会进行缓存，超过一定时间才会清理掉
        public void UnloadUnused(bool clearAll = false)
        {
            foreach (var abLoader in abLoaders)
            {
                if (clearAll)
                {
                    abLoader.UnloadUnusedTotal();
                }
                else
                {
                    //有链式依赖的可能，尝试释放2次
                    //TODO，增加接口，释放时尝试释放依赖的资源
                    for (int i=0; i<2; i++)
                        abLoader.UnloadUnused();
                }
            }
        }

        public IEnumerator LoadScene(string scenePath,bool clearUnused = false)
        {
            //Debug.Log("Load Scene Begin========");
            weakRefs.ClearRef();
            yield return SceneManager.LoadSceneAsync("empty");
            UnloadUnused(clearUnused);
            yield return null;
            var loadHandle = GetLoadHandle(scenePath);
            yield return loadHandle.LoadScene(LoadSceneMode.Single);
            //Debug.Log("Load Scene end==========");
        }

        //缓存资源，切场景时自动释放
        public void WeakCache(AssetHandle assetHandle)
        {
            weakRefs.AddRef(assetHandle.assetRef);
        }

        //释放缓存的弱引用资源
        public void ReleaseWeakCache(AssetHandle assetHandle)
        {
            weakRefs.RemoveRef(assetHandle.assetRef);
        }

        //缓存资源，AssetManager被销毁时才释放，提前释放需要手动释放
        //慎重调用
        public void CacheAsset(AssetHandle assetHandle)
        {
            strongRefs.AddRef(assetHandle.assetRef);
        }

        //释放强引用资源
        public void ReleaseCache(AssetHandle assetHandle)
        {
            strongRefs.RemoveRef(assetHandle.assetRef);
        }

        IEnumerator AssetLoadProcess()
        {
            while (true)
            {
                int frameLoadCount = 0;
                while (!requestQueue.IsEmpty())
                {
                    if (frameLoadCount > maxLoadCountPerFrame)
                    {
                        frameLoadCount = 0;
                        yield return null;
                    }
                    else
                    {
                        var req = requestQueue.Dequeue();
                        bool loadSucc = false;
                        if (req.priority >= AssetLoadRequest.Priority_Fast)
                        {
                            loadSucc = DoLoadAsync(req);
                        }
                        else
                        {
                            loadSucc = DoLoadSync(req);
                        }
                        if (loadSucc)
                        {
                            frameLoadCount++;
                        }
                    }
                }
                yield return null;
            }
        }

        bool DoLoadAsync(AssetLoadRequest req)
        {
            if (!req.isUrl)
            {
                var handle = GetLoadHandle(req.path);
                return handle.LoadAsync(req);
            }        
            else
            {
                var group = req.GetData<string>("__webGroup");
                if (webLoaders.TryGetValue(group,out var loader))
                {
                    loader.Load(req);
                    return true;
                }
                return false;
            }
        }

        bool DoLoadSync(AssetLoadRequest req)
        {
            var handle = GetLoadHandle(req.path);
            req.assetHandle = handle.Load(req.type);
            req.Complete();
            return req.assetHandle.isValid;
        }

        private LoadHandle GetLoadHandle(string assetPath)
        {
            LoadHandle handle;
            //有缓存直接返回
            if (loadHandleCache.TryGetValue(assetPath, out handle))
            {
                return handle;
            }

            #if UNITY_EDITOR
            if (isEditorMode)
            {
                handle = new EditorLoadHandle()
                {
                    assetPath = assetPath,
                };
            }
            else
            #endif
            {
                string lowerPath = assetPath.ToLower();

                //通过path，得到ab完整路径，已经对应的ab里的assetName
                //GetAbPath(path,out var abPath,out var assetName);

                string abPath = null;
                string assetName = null;
                AbLoader abLoader = abLoaders.Find((loader) => { return loader.GetAbPath(assetPath,out abPath,out assetName); });
                if (abLoader == null)
                {
                    //throw new Exception("path not valid:" + assetPath);
                    Debug.LogError("path not valid:" + assetPath);
                    return handle = new InvalidLoadHandle();
                }

                handle = new AbHandle()
                {
                    abLoader = abLoader,
                    assetName = assetName,
                    abPath = abPath,
                };
            }


            loadHandleCache[assetPath] = handle;
            return handle;
        }

        //关联资源引用到GameObject
        public void AddAssetRefToGameObject(GameObject gameObject, AssetRef assetRef)
        {
            if (gameObject == null)
            {
                return;
            }
            var helper = gameObject.GetComponent<AssetRefHelper>();
            if (helper == null)
            {
                helper = gameObject.AddComponent<AssetRefHelper>();
            }
            helper.RefAsset(assetRef);
        }

        public void RemoveAssetRef(GameObject gameObject,UnityEngine.Object asset)
        {
            var assetRef = GetAssetRef(asset);
            if (assetRef == null)
            {
                return;
            }
            var helper = gameObject.GetComponent<AssetRefHelper>();
            if (helper != null)
            {
                helper.UnRef(assetRef);
            }
        }

        public void AddAssetRefToGameObject(GameObject gameObject, UnityEngine.Object asset)
        {
            var assetRef = GetAssetRef(asset);
            if (assetRef != null)
            {
                AddAssetRefToGameObject(gameObject, assetRef);
            }
        }

        //替换资源引用时的辅助接口
        public void ReplaceGameObjectAssetRef(GameObject gameObject, UnityEngine.Object newAsset, UnityEngine.Object oldAsset)
        {
            if (newAsset == oldAsset)
            {
                return;
            }
            if (gameObject == null)
            {
                return;
            }
            AddAssetRefToGameObject(gameObject, newAsset);
            var helper = gameObject.GetComponent<AssetRefHelper>();
            if (helper != null)
            {
                helper.UnRef(GetAssetRef(oldAsset));
            }
        }

        //通过asset查询关联的引用
        public AssetRef GetAssetRef(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return null;
            }
            int instanceId = asset.GetInstanceID();
            if (assetRefDict.TryGetValue(instanceId, out var abRef))
            {
                if (!abRef.valid)
                {
                    assetRefDict.Remove(instanceId);
                    return null;
                }
                return abRef;
            }
            return null;
        }

        internal void OnLoadAssetSucc(AssetHandle assetHandle)
        {
            assetRefDict[assetHandle.asset.GetInstanceID()] = assetHandle.assetRef;
        }

        List<int> tempRefs = new List<int>();
        private void ClearInvalidAssetRef()
        {
            //清除失效的资源到ab引用映射
            tempRefs.Clear();
            foreach (var pair in assetRefDict)
            {
                if (!pair.Value.valid) //引用已经失效了，不再被管理了
                {
                    tempRefs.Add(pair.Key);
                }
            }
            foreach (var key in tempRefs)
            {
                assetRefDict.Remove(key);
            }
            tempRefs.Clear();
        }

        IEnumerator AutoClearInvalidAssetRef()
        {
            while (true)
            {
                ClearInvalidAssetRef();
                yield return new WaitForSeconds(10f);
            }
        }

        #if UNITY_EDITOR
        #region editor load
        AssetRefManager editorAssetRefMagr = new AssetRefManager();
        private string[] tmpStrArr = new string[1];
        //"Assets/Bundle/Test" -> "Assets/Bundle/Test.prefab
        private string GetRealPath(string assetPath, System.Type type)
        {
            string assetName = Path.GetFileName(assetPath);
            string searchName = assetName;
            if (type != null)
            {
                searchName += " t:" + Path.GetExtension(type.ToString()).TrimStart('.'); // UnityEngine.GameObject -> GameObject
            }
            tmpStrArr[0] = Path.GetDirectoryName(assetPath);
            var files = AssetDatabase.FindAssets(searchName, tmpStrArr);
            if (files.Length == 0)
            {
                return assetPath;
            }
            else if (files.Length == 1)
            {
                return AssetDatabase.GUIDToAssetPath(files[0]);
            }
            else
            {
                //相同文件夹下，多个文件名字有包含关系
                //Test.png,Test_1.png
                foreach (var guid in files)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (Path.GetFileNameWithoutExtension(path) == assetName)
                    {
                        return path;
                    }
                }
            }
            return assetPath;
        }

        AssetHandle LoadAssetAtEditor(string assetPath, System.Type type)
        {
            string realPath = Path.HasExtension(assetPath) ? assetPath : GetRealPath(assetPath, type);
            var asset = AssetDatabase.LoadAssetAtPath(realPath, type == null ? typeof(UnityEngine.Object) : type);
            if (asset == null)
            {
                Debug.LogWarning("load failed:" + assetPath);
                return AssetHandle.invalid;
            }
            return new AssetHandle(asset, editorAssetRefMagr.GetOrCreateRef(asset));
        }
        #endregion
#endif

        public bool Contains(string assetPath)
        {
            bool contains = false;
            for ( int i =0; i < abLoaders.Count; ++i )
            {
                if ( abLoaders[i].ContainAsset(assetPath) )
                {
                    contains = true;
                    break;
                }
            }
            return contains;
        }

        public AssetLoadRequest LoadWebTexture(string group,string url
            ,GameObject autoRefGameObject = null
            ,AssetLoadRequest.OnCompleted cb = null
            ,int priority = AssetLoadRequest.Priority_Common)
        {
            if (priority <= AssetLoadRequest.Priority_Fast)
            {
                throw new Exception("Load From web not support sync mode." + url);
            }
            AssetLoadRequest req = AssetLoadRequest.Get();
            req.url = url;
            req.autoRefGameObject = autoRefGameObject;
            req.priority = priority;
            req.type = typeof(Texture2D);
            req.onCompleted = cb;
            req.AttachData("__webGroup",group);
            StartLoad(req);
            return req;
        }

        public AssetLoadRequest LoadFromWeb(string group,UnityEngine.Networking.UnityWebRequest webReq
            ,GameObject autoRefGameObject = null
            ,AssetLoadRequest.OnCompleted cb = null
            ,int priority = AssetLoadRequest.Priority_Common
            )
        {
            if (priority <= AssetLoadRequest.Priority_Fast)
            {
                throw new Exception("Load From web not support sync mode." + webReq.url);
            }
            AssetLoadRequest req = AssetLoadRequest.Get();
            req.url = webReq.url;
            req.autoRefGameObject = autoRefGameObject;
            req.priority = priority;
            req.onCompleted = cb;
            req.AttachData("__webReq",webReq);
            req.AttachData("__webGroup",group);
            StartLoad(req);
            return req;
        }

        public void AddWebLoader(string groupName,int cacheSize)
        {
            if (webLoaders.ContainsKey(groupName))
            {
                Debug.LogError("already has group name:" + groupName);
                return;
            }
            var loader = new WebLoader();
            loader.Init(cacheSize);
            webLoaders[groupName] = loader;
        }

#if UNITY_EDITOR
    #region 编辑器相关接口，获取编辑器用数据
    public class AssetInfo
    {
        public UnityEngine.Object asset;            //资源（AssetBundle)
        public AssetRef assetRef;                   //资源引用
        public GameObject[] refGameObjects;         //引用该资源的GameObject(通过AssetRefHelper自动引用的)
        public bool isCacheWeak;                    //是否被弱引用
        public bool isCacheStrong;                  //是否被强引用
        public bool isInLru;                        //是否在Lru缓存里
        public List<AssetInfo> refs;                //引用（依赖）的资源
        public List<AssetInfo> refedByOthers;       //引用（依赖）该资源的其他资源
    }

    public struct AssetFrameInfo
    {
        public AssetInfo[] assetInfos;              //所有的资源
        public float cacheHitPercent;               //缓存命中率
    }

    //抓取信息，一帧调用一次即可
    public AssetFrameInfo CaptureFrame()
    {
        //所有的资源
        Dictionary<UnityEngine.Object,AssetInfo> assetMap = new Dictionary<UnityEngine.Object, AssetInfo>();
        //依赖关系
        Dictionary<UnityEngine.Object,AssetRefList> depMap = new Dictionary<UnityEngine.Object, AssetRefList>();

        float cacheHitPercent = 0.0f;
        foreach(var abLoader in abLoaders)
        {
            var lruInfo = new HashSet<string>(abLoader.GetLruInfo());
            cacheHitPercent += abLoader.cacheHitPercent;
            foreach(var abInfo in abLoader.GetLoadedAbs())
            {
                if (abInfo == null)
                {
                    continue;
                }
                AssetInfo info = new AssetInfo();
                info.asset = abInfo.ab;
                info.assetRef = GetAssetRef(abInfo.ab);
                if (info.assetRef != null)
                {
                    info.isCacheWeak = weakRefs.Contains(info.assetRef);
                    info.isCacheStrong = strongRefs.Contains(info.assetRef);
                    info.refGameObjects = AssetRefHelper.FindGameObjects(info.assetRef);
                }
                info.isInLru = lruInfo.Contains(abInfo.abPath);
                assetMap[info.asset] = info;
                depMap[info.asset] = abInfo.deps;
            }
        }

        //整理依赖关系，构造双向查询
        foreach(var pair in assetMap)
        {
            var info = pair.Value;
            var asset = pair.Key;

            if (depMap.TryGetValue(asset,out var deps))
            {
                info.refs = new List<AssetInfo>();
                foreach(var dep in deps.ToArray())
                {
                    var depInfo = assetMap[dep.asset];  //必须可以取到
                    info.refs.Add(depInfo);
                    if (depInfo.refedByOthers == null) depInfo.refedByOthers = new List<AssetInfo>();
                    depInfo.refedByOthers.Add(info);
                }
            }
        }

        AssetFrameInfo frameInfo = new AssetFrameInfo();
        frameInfo.assetInfos = assetMap.Values.ToArray();
        frameInfo.cacheHitPercent = cacheHitPercent / abLoaders.Count;
        return frameInfo;
    }

    #endregion
#endif
    }
}

