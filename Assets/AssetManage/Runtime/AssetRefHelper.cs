#if UNITY_EDITOR
using System.Linq;
#endif
using System.Collections.Generic;
using UnityEngine;
using FunPlus.AssetManage;

namespace FunPlus.AssetManage
{
    public class AssetRefHelper : MonoBehaviour
    {
        private AssetRefList refList;

        public void RefAsset(AssetRef assetRef)
        {
            if (refList == null)
            {
                refList = new AssetRefList();
            }
            refList.AddRef(assetRef);
#if UNITY_EDITOR
            OnRef(this, assetRef);
#endif
        }

        public void UnRef(AssetRef assetRef)
        {
            if (assetRef == null)
            {
                return;
            }
            if (refList != null)
            {
                return;
            }
            refList.RemoveRef(assetRef);
#if UNITY_EDITOR
            OnUnRef(this, assetRef);
#endif
        }

        void OnDestroy()
        {
#if UNITY_EDITOR
            OnClear(this);
#endif
            if (refList != null)
            {
                refList.ClearRef();
            }
        }

#if UNITY_EDITOR
        private static Dictionary<AssetRef, HashSet<GameObject>> assetRefDict = new Dictionary<AssetRef, HashSet<GameObject>>();
        private static void OnRef(AssetRefHelper helper, AssetRef assetRef)
        {
            if (assetRefDict.TryGetValue(assetRef, out var set))
            {
                set.Add(helper.gameObject);
            }
            else
            {
                set = new HashSet<GameObject>();
                set.Add(helper.gameObject);
                assetRefDict[assetRef] = set;
            }
        }

        private static void OnUnRef(AssetRefHelper helper, AssetRef assetRef)
        {
            if (assetRefDict.TryGetValue(assetRef, out var set))
            {
                set.Remove(helper.gameObject);
            }
        }

        private static void OnClear(AssetRefHelper helper)
        {
            foreach (var assetRef in helper.refList.ToArray())
            {
                OnUnRef(helper, assetRef);
            }
        }

        public static GameObject[] FindGameObjects(AssetRef assetRef)
        {
            if (assetRefDict.TryGetValue(assetRef, out var set))
            {
                return set.ToArray();
            }
            return null;
        }

        private void OnApplicationQuit()
        {
            assetRefDict.Clear();
        }
#endif
    }
}
