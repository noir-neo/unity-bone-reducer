using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace BoneReducer.Editor
{
    public class BoneReducer : EditorWindow
    {
        [MenuItem("Window/BoneReducer/BoneReducer")]
        public static void Init()
        {
            var wnd = GetWindow<BoneReducer>();
            wnd.titleContent = new GUIContent(nameof(BoneReducer));
        }

        ObjectField rootObjectField;
        VisualElement popupsPlaceholder;
        PopupField<Transform> targetBonePopup;
        PopupField<Transform> mergeWeightTargetBonePopup;
        TextField outputPathSuffixField;
        Button button;

        public void OnEnable()
        {
            var root = rootVisualElement;
            rootObjectField = new ObjectField("Root") {objectType = typeof(GameObject), allowSceneObjects = true};
            root.Add(rootObjectField);

            popupsPlaceholder = new VisualElement();
            root.Add(popupsPlaceholder);

            outputPathSuffixField = new TextField("Output path suffix");
            root.Add(outputPathSuffixField);

            button = new Button(OnButtonClick) {text = "Delete the bone"};
            root.Add(button);

            rootObjectField.RegisterValueChangedCallback(OnSkinnedMeshRendererChanged);
        }

        public void OnDisable()
        {
            targetBonePopup?.UnregisterValueChangedCallback(OnTargetBoneChanged);
            rootObjectField.UnregisterValueChangedCallback(OnSkinnedMeshRendererChanged);
        }

        void OnButtonClick()
        {
            var skinnedMeshRenderers = ((GameObject) rootObjectField.value).GetComponentsInChildren<SkinnedMeshRenderer>();
            DeleteBone(skinnedMeshRenderers,
                targetBonePopup.value,
                mergeWeightTargetBonePopup.value,
                outputPathSuffixField.value);
            UpdatePopups(skinnedMeshRenderers, targetBonePopup.index, mergeWeightTargetBonePopup.index);
        }

        void OnSkinnedMeshRendererChanged(ChangeEvent<Object> e)
        {
            if (e.newValue == null)
            {
                UpdatePopups(null);
                return;
            }

            var skinnedMeshRenderers = ((GameObject) e.newValue).GetComponentsInChildren<SkinnedMeshRenderer>();

            UpdatePopups(skinnedMeshRenderers);
        }

        void UpdatePopups(SkinnedMeshRenderer[] skinnedMeshRenderers, int targetBoneIndex = 0, int mergeWeightTargetBoneIndex = 0)
        {
            if (targetBonePopup != null)
            {
                targetBonePopup.UnregisterValueChangedCallback(OnTargetBoneChanged);
                popupsPlaceholder.Remove(targetBonePopup);
                targetBonePopup = null;
            }

            if (mergeWeightTargetBonePopup != null)
            {
                popupsPlaceholder.Remove(mergeWeightTargetBonePopup);
                mergeWeightTargetBonePopup = null;
            }

            if (skinnedMeshRenderers == null)
            {
                return;
            }

            var bones = skinnedMeshRenderers.SelectMany(r => r.bones).Distinct().ToList();
            targetBonePopup = new PopupField<Transform>("Target bone",
                bones,
                Math.Min(bones.Count, targetBoneIndex),
                t => t.name);
            targetBonePopup.RegisterValueChangedCallback(OnTargetBoneChanged);
            popupsPlaceholder.Add(targetBonePopup);

            mergeWeightTargetBonePopup = new PopupField<Transform>("Merge bone weight into",
                bones,
                Math.Min(bones.Count, mergeWeightTargetBoneIndex),
                t => t.name);
            popupsPlaceholder.Add(mergeWeightTargetBonePopup);
        }

        void OnTargetBoneChanged(ChangeEvent<Transform> e)
        {
            if (e.newValue == null || rootObjectField.value == null)
            {
                return;
            }

            var skinnedMeshRenderer = rootObjectField.value as SkinnedMeshRenderer;
            if (skinnedMeshRenderer == null) return;
            var parentBone = ParentBone(skinnedMeshRenderer.bones, e.newValue);
            if (parentBone != null) mergeWeightTargetBonePopup.value = parentBone;
        }

        static string OutputPath(Object obj, string suffix)
        {
            var originalAssetPath = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(originalAssetPath)) return "";
            var isSubAsset = AssetDatabase.IsSubAsset(obj);
            var directoryName = Path.GetDirectoryName(originalAssetPath);
            var fileName = isSubAsset
                ? $"{Path.GetFileNameWithoutExtension(originalAssetPath)}_{obj.name}{suffix}.asset"
                : $"{Path.GetFileNameWithoutExtension(originalAssetPath)}{suffix}.asset";
            return Path.Combine(directoryName, fileName);
        }

        static void DeleteBone(IEnumerable<SkinnedMeshRenderer> skinnedMeshRenderers, Transform targetBone,
            Transform mergeWeightTargetBone, string outputPathSuffix)
        {
            foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
            {
                DeleteBone(skinnedMeshRenderer, targetBone, mergeWeightTargetBone, outputPathSuffix);
            }
        }

        static void DeleteBone(SkinnedMeshRenderer skinnedMeshRenderer, Transform targetBone,
            Transform mergeWeightTargetBone, string outputPathSuffix)
        {
            var outputPath = OutputPath(skinnedMeshRenderer.sharedMesh, outputPathSuffix);
            var mesh = Instantiate(skinnedMeshRenderer.sharedMesh);
            var bones = skinnedMeshRenderer.bones.ToList();
            var targetBoneIndex = bones.IndexOf(targetBone);
            if (targetBoneIndex < 0) return;
            var mergeWeightTargetBoneIndex = bones.IndexOf(mergeWeightTargetBone);
            if (mergeWeightTargetBoneIndex < 0)
            {
                skinnedMeshRenderer.bones = skinnedMeshRenderer.bones
                    .Select(b => b == targetBone ? mergeWeightTargetBone : b)
                    .ToArray();
                var matrix = mergeWeightTargetBone.worldToLocalMatrix * skinnedMeshRenderer.transform.localToWorldMatrix;
                mesh.bindposes = mesh.bindposes
                    .Select((m, i) => i == targetBoneIndex ? matrix : m)
                    .ToArray();
            }
            else
            {
                skinnedMeshRenderer.bones = skinnedMeshRenderer.bones
                    .Where((_, i) => i != targetBoneIndex)
                    .ToArray();
                mesh.boneWeights = mesh.boneWeights
                    .Select(x => MergeBoneWeight(x, targetBoneIndex, mergeWeightTargetBoneIndex))
                    .Select(x => DeleteBoneIndex(x, targetBoneIndex))
                    .ToArray();
                mesh.bindposes = mesh.bindposes
                    .Where((_, i) => i != targetBoneIndex)
                    .ToArray();
            }

            skinnedMeshRenderer.sharedMesh = mesh;
            EditorUtility.SetDirty(skinnedMeshRenderer);

            if (!string.IsNullOrEmpty(outputPath))
            {
                AssetDatabase.CreateAsset(mesh, outputPath);
                AssetDatabase.SaveAssets();
            }
        }

        static Transform ParentBone(Transform[] bones, Transform target)
        {
            while (true)
            {
                var parent = target.parent;
                if (bones.Contains(parent))
                {
                    return parent;
                }

                target = parent;
            }
        }

        static BoneWeight MergeBoneWeight(in BoneWeight boneWeight, int fromIndex, int toIndex)
        {
            var boneWeights = new (int index, float weight)[]
            {
                (boneWeight.boneIndex0, boneWeight.weight0),
                (boneWeight.boneIndex1, boneWeight.weight1),
                (boneWeight.boneIndex2, boneWeight.weight2),
                (boneWeight.boneIndex3, boneWeight.weight3)
            }
                .Select(t => t.index == fromIndex ? (index: toIndex, t.weight) : t)
                .GroupBy(t => t.index)
                .Select(g => (index: g.Key, weight: g.Sum(x => x.weight)))
                .Concat(Enumerable.Repeat((index: 0, weight: 0f), 4))
                .Take(4)
                .ToArray();
            return new BoneWeight
            {
                boneIndex0 = boneWeights[0].index,
                weight0 = boneWeights[0].weight,
                boneIndex1 = boneWeights[1].index,
                weight1 = boneWeights[1].weight,
                boneIndex2 = boneWeights[2].index,
                weight2 = boneWeights[2].weight,
                boneIndex3 = boneWeights[3].index,
                weight3 = boneWeights[3].weight
            };
        }

        static BoneWeight DeleteBoneIndex(in BoneWeight boneWeight, int targetIndex)
        {
            return new BoneWeight
            {
                boneIndex0 = boneWeight.boneIndex0 - (boneWeight.boneIndex0 > targetIndex ? 1 : 0),
                weight0 = boneWeight.weight0,
                boneIndex1 = boneWeight.boneIndex1 - (boneWeight.boneIndex1 > targetIndex ? 1 : 0),
                weight1 = boneWeight.weight1,
                boneIndex2 = boneWeight.boneIndex2 - (boneWeight.boneIndex2 > targetIndex ? 1 : 0),
                weight2 = boneWeight.weight2,
                boneIndex3 = boneWeight.boneIndex3 - (boneWeight.boneIndex3 > targetIndex ? 1 : 0),
                weight3 = boneWeight.weight3
            };
        }
    }
}