using System.Linq;
using System;
using System.Collections.Generic;
using UnityEngine.Networking;
using UnityEngine;
using Object = UnityEngine.Object;

namespace FunPlus.AssetManage
{
    public class WebLoader
    {
        private class WebAssetInfo
        {
            public Object asset;
            public int unusedFrame;
        }

        private AssetRefManager refMgr = new AssetRefManager();
        private Dictionary<string, WebAssetInfo> loaded = new Dictionary<string, WebAssetInfo>();
        private LRUCache lruCache;
        private Dictionary<string,List<AssetLoadRequest>> loadingReqs = new Dictionary<string, List<AssetLoadRequest>>();

        public void Init(int cacheSize)
        {
            lruCache = new LRUCache(cacheSize);
            loaded.Clear();
        }

        public void Load(AssetLoadRequest req)
        {
            string url = req.url;

            if (loaded.TryGetValue(url, out var info))
            {
                //已经加载完了
                info.unusedFrame = 0;
                req.assetHandle = new AssetHandle(info.asset, refMgr.GetOrCreateRef(info.asset));
                req.Complete();
                return;
            }

            var webReq = req.GetData<UnityWebRequest>("__webRequest");
            if (webReq == null)
            {
                webReq = UnityWebRequest.Get(url);
            }
            var opt = webReq.SendWebRequest();
            opt.completed += OnRequestCompelted;
            
            if (loadingReqs.TryGetValue(url,out var reqs))
            {
                reqs.Add(req);
            }
            else
            {
                reqs = new List<AssetLoadRequest>();
                reqs.Add(req);
                loadingReqs[url] = reqs;
            }
        }

        private void OnRequestCompelted(AsyncOperation opt)
        {
            var webOpt = opt as UnityWebRequestAsyncOperation;
            if (webOpt == null)
            {
                return;
            }
            var webReq = webOpt.webRequest;
            if (webReq == null)
            {
                return;
            }
            if (!loadingReqs.TryGetValue(webReq.url,out var reqs))
            {
                return;
            }
            if (reqs.Count == 0)
            {
                return;
            }
            foreach(var req in reqs)
            {
                if (webReq.isHttpError || webReq.isNetworkError)
                {
                    Debug.LogError(webReq.error);
                }
                else
                {
                    if (HandleAsset(webReq, req.type, out var asset))
                    {
                        //unity内置格式
                        OnLoadCompleted(asset, req);
                    }
                    else
                    {
                        //原始byte
                        req.AttachData("__webBytes", webOpt.webRequest.downloadHandler.data);
                    }
                }
                req.Complete();
            }
            webReq.Dispose();
            loadingReqs.Remove(webReq.url);
        }

        private bool HandleAsset(UnityWebRequest webReq, Type type, out Object asset)
        {
            if (type == typeof(Texture))
            {
                asset = DownloadHandlerTexture.GetContent(webReq);
                return true;
            }
            else if (type == typeof(TextAsset))
            {
                string content = webReq.downloadHandler.text;
                asset = new TextAsset(content);
                return true;
            }
            else
            {
                asset = null;
                return false;
            }
        }

        private void OnLoadCompleted(Object asset, AssetLoadRequest req)
        {
            loaded[req.path] = new WebAssetInfo()
            {
                asset = asset,
                unusedFrame = 0,
            };
            req.assetHandle = new AssetHandle(asset, refMgr.GetOrCreateRef(asset));
            req.Complete();
        }

        private void Unload(string url)
        {
            if (!loaded.TryGetValue(url, out var info))
            {
                return;
            }

            var asset = info.asset;
            refMgr.DestroyRef(asset);
            loaded.Remove(url);
            if (asset != null)
            {
                Object.Destroy(asset);
            }
        }

        private List<string> tmps = new List<string>();
        public void UnloadUnused()
        {
            tmps.Clear();
            int currFrameCount = Time.frameCount;
            foreach (var pair in loaded)
            {
                var info = pair.Value;

                if (refMgr.HasRef(info.asset))
                {
                    continue;
                }

                if (info.unusedFrame == 0)
                {
                    info.unusedFrame = currFrameCount;
                    lruCache.Put(pair.Key);
                }
                if (currFrameCount - info.unusedFrame < 30)
                {
                    continue;
                }

                tmps.Add(pair.Key);
            }

            foreach (var key in tmps)
            {
                if (lruCache.Contains(key))
                {
                    continue;
                }
                Unload(key);
            }
            tmps.Clear();
        }
    }
}