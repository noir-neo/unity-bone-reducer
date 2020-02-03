using System;
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

        ObjectField skinnedMeshRendererField;
        VisualElement popupsPlaceholder;
        PopupField<Transform> targetBonePopup;
        PopupField<Transform> mergeWeightTargetBonePopup;
        TextField outputPathField;
        Button button;

        public void OnEnable()
        {
            var root = rootVisualElement;
            skinnedMeshRendererField = new ObjectField("Skinned Mesh Renderer") {objectType = typeof(SkinnedMeshRenderer), allowSceneObjects = true};
            root.Add(skinnedMeshRendererField);

            popupsPlaceholder = new VisualElement();
            root.Add(popupsPlaceholder);

            outputPathField = new TextField("Output path");
            root.Add(outputPathField);

            button = new Button(OnButtonClick) {text = "Delete the bone"};
            root.Add(button);

            skinnedMeshRendererField.RegisterValueChangedCallback(OnSkinnedMeshRendererChanged);
        }

        public void OnDisable()
        {
            targetBonePopup?.UnregisterValueChangedCallback(OnTargetBoneChanged);
            skinnedMeshRendererField.UnregisterValueChangedCallback(OnSkinnedMeshRendererChanged);
        }

        void OnButtonClick()
        {
            var skinnedMeshRenderer = skinnedMeshRendererField.value as SkinnedMeshRenderer;
            var targetBoneIndex = targetBonePopup.index;
            var mergeWeightTargetBoneIndex = mergeWeightTargetBonePopup.index;
            var outputPath = outputPathField.value;
            DeleteBone(skinnedMeshRenderer,
                targetBoneIndex,
                mergeWeightTargetBoneIndex,
                outputPath);
            UpdatePopups(skinnedMeshRenderer, targetBoneIndex, mergeWeightTargetBoneIndex);
        }

        void OnSkinnedMeshRendererChanged(ChangeEvent<Object> e)
        {
            if (e.newValue == null)
            {
                UpdatePopups(null);
                return;
            }

            var skinnedMeshRenderer = e.newValue as SkinnedMeshRenderer;

            UpdatePopups(skinnedMeshRenderer);

            var mesh = skinnedMeshRenderer.sharedMesh;
            outputPathField.value = OutputPath(mesh);
        }

        void UpdatePopups(SkinnedMeshRenderer skinnedMeshRenderer, int targetBoneIndex = 0, int mergeWeightTargetBoneIndex = 0)
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

            if (skinnedMeshRenderer == null)
            {
                return;
            }

            var bones = skinnedMeshRenderer.bones.ToList();
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
            if (e.newValue == null || skinnedMeshRendererField.value == null)
            {
                return;
            }

            var skinnedMeshRenderer = skinnedMeshRendererField.value as SkinnedMeshRenderer;
            var targetBone = e.newValue;
            mergeWeightTargetBonePopup.value = ParentBone(skinnedMeshRenderer.bones, targetBone);
        }

        static string OutputPath(Object obj)
        {
            var originalAssetPath = AssetDatabase.GetAssetPath(obj);
            var isSubAsset = AssetDatabase.IsSubAsset(obj);
            var directoryName = Path.GetDirectoryName(originalAssetPath);
            var fileName = isSubAsset
                ? $"{Path.GetFileNameWithoutExtension(originalAssetPath)}_{obj.name}_BoneReduced.asset"
                : $"{Path.GetFileNameWithoutExtension(originalAssetPath)}_BoneReduced.asset";
            return Path.Combine(directoryName, fileName);
        }

        static void DeleteBone(SkinnedMeshRenderer skinnedMeshRenderer, int targetBoneIndex, int mergeWeightTargetBoneIndex, string outputPath)
        {
            var mesh = Instantiate(skinnedMeshRenderer.sharedMesh);
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

            skinnedMeshRenderer.sharedMesh = mesh;
            AssetDatabase.CreateAsset(mesh, outputPath);
            AssetDatabase.SaveAssets();
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