﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Trackman;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace Figma
{
    using Attributes;

    public static class VisualElementMetadata
    {
        #region Fields
        static Dictionary<VisualElement, (UIDocument document, UxmlAttribute uxml, string path)> rootMetadata = new Dictionary<VisualElement, (UIDocument document, UxmlAttribute uxml, string path)>();
        static List<VisualElement> search = new List<VisualElement>(256);
        static Dictionary<VisualElement, string> cloneMap = new Dictionary<VisualElement, string>(256);

        static WaitForEndOfFrame waitForEndOfFrame = new WaitForEndOfFrame();
        #endregion

        #region Properties
        public static BindingFlags FieldsFlags => BindingFlags.NonPublic | BindingFlags.Instance;
        public static BindingFlags MethodsFlags => BindingFlags.NonPublic | BindingFlags.Instance;
        #endregion

        #region Callbacks
        public static Action<VisualElement, UIDocument, UxmlAttribute> OnInitializeRoot { get; set; }
        public static Action<VisualElement, object, Type, FieldInfo, QueryAttribute, QueryAttribute> OnInitializeElement { get; set; }
        public static Action<VisualElement> OnRebuildElement { get; set; }
        #endregion

        #region Methods
        public static void Initialize(UIDocument document, IRootElement[] targets)
        {
            foreach (IRootElement target in targets)
                if (target is not null)
                    Initialize(document, target);
        }
        public static void Initialize(UIDocument document, IRootElement target)
        {
            Type targetType = target.GetType();
            UxmlAttribute uxml = targetType.GetCustomAttribute<UxmlAttribute>();
            VisualElement targetRoot = document.rootVisualElement.Find(uxml.DocumentRoot, throwException: false, silent: false);
            (string path, VisualElement element)[] rootsPreserved = uxml.DocumentPreserve.Select(x => (x, document.rootVisualElement.Find(x, throwException: false, silent: false))).ToArray();
            if (targetRoot is null) return;

            rootMetadata.Add(targetRoot, (document, uxml, uxml.DocumentRoot));
            foreach ((string path, VisualElement element) value in rootsPreserved) if (!rootMetadata.ContainsKey(value.element)) rootMetadata.Add(value.element, (document, uxml, value.path));

            OnInitializeRoot?.Invoke(targetRoot, document, uxml);
            Initialize(target, targetType, targetRoot);
            target.OnInitialize(targetRoot, rootsPreserved.Select(x => x.element).ToArray());
        }
        public static void Initialize(ISubElement target, VisualElement root)
        {
            Type targetType = target.GetType();
            Initialize(target, targetType, root);
            target.OnInitialize();
        }
        public static void Rebuild(IRootElement[] targets)
        {
            foreach (IRootElement target in targets)
            {
                if (target.Root is null) continue;
                target.OnRebuild();
                foreach (VisualElement child in target.Root.Children()) Rebuild(child);
            }
        }
        public static void Rebuild(VisualElement target)
        {
            if (target is ISubElement targetSubElement) targetSubElement.OnRebuild();
            foreach (VisualElement child in target.Children()) Rebuild(child);

            OnRebuildElement(target);
        }

        public static IEnumerable<T> Search<T>(this VisualElement value, string path, string className = default) where T : VisualElement
        {
            static bool StartsWith(string path, VisualElement value, int startIndex)
            {
                int endIndex = startIndex + value.name.Length;
                return path.BeginsWith(value.name, startIndex) && path.Length >= endIndex && (path.Length == endIndex || path[endIndex].IsSeparator());
            }
            static int LastIndexOf(VisualElement root, VisualElement leaf, VisualElement value, string path, int startIndex = 0)
            {
                if (value.parent is not null && value.parent != root) startIndex = LastIndexOf(root, leaf, value.parent, path, startIndex);
                if (startIndex >= 0 && StartsWith(path, value, startIndex))
                {
                    int endIndex = startIndex + value.name.Length;
                    if (path.Length > endIndex && path[endIndex].IsSeparator() && value != leaf) endIndex++;
                    return endIndex;
                }

                return -1;
            }
            static void Search(VisualElement root, VisualElement value, string path, int startIndex = 0, string className = default)
            {
                static bool EqualsTo(VisualElement value, string path, int startIndex)
                {
                    return path.EqualsTo(value.name, startIndex);
                }

                foreach (VisualElement child in value.Children())
                {
                    if (child.name.NotNullOrEmpty() && EqualsTo(child, path, startIndex) && (className.NullOrEmpty() || child.ClassListContains(className))) search.Add(child);
                }

                foreach (VisualElement child in value.Children())
                {
                    if (child.name.NotNullOrEmpty() && StartsWith(path, child, startIndex))
                        Search(root, child, path, startIndex + child.name.Length + 1, className);
                }
            }
            static void SearchByFullPath(VisualElement value, string path, int startIndex = 0, string className = default)
            {
                static bool EqualsToFullPath(VisualElement root, VisualElement value, string path, int startIndex)
                {
                    return LastIndexOf(root, value, value, path, startIndex) == path.Length;
                }
                static bool StartsWithFullPath(VisualElement root, VisualElement value, string path, int startIndex)
                {
                    int endIndex = LastIndexOf(root, value, value, path, startIndex);
                    return endIndex >= 0 && path.Length > endIndex && path[endIndex].IsSeparator();
                }

                foreach (VisualElement child in value.Children())
                {
                    if (child.name.NotNullOrEmpty() && EqualsToFullPath(value, child, path, startIndex) && (className.NullOrEmpty() || child.ClassListContains(className))) search.Add(child);
                }

                foreach (VisualElement child in value.Children())
                {
                    if (child.name.NotNullOrEmpty() && StartsWithFullPath(value, child, path, startIndex))
                        SearchByFullPath(child, path, startIndex + child.name.Length + 1, className);
                }
            }

            search.Clear();

            VisualElement root = FindRoot(value);
            if (root is not null)
            {
                UxmlAttribute uxml = rootMetadata[root].uxml;
                if (path.BeginsWith(uxml.DocumentRoot) || uxml.DocumentPreserve.Any(x => path.BeginsWith(x))) SearchByFullPath(root.parent.parent.parent, path, 0, className);
                else Search(value, value, path, 0, className);
            }
            else
            {
                SearchByFullPath(value, path, 0, className);
            }

            foreach (T result in search) yield return result;
        }
        public static T Find<T>(this VisualElement value, string path, string className = default, bool throwException = true, bool silent = false) where T : VisualElement
        {
            T result = value.Search<T>(path, className).FirstOrDefault();
            if (result is not null)
            {
                return result;
            }
            else if (throwException)
            {
                throw new Exception($"Cannot find {typeof(T).Name} [<color=yellow>{path}</color>]");
            }
            else
            {
                if (!silent) Debug.LogWarning($"Cannot find {typeof(T).Name} [<color=yellow>{path}</color>]");
                return default;
            }
        }
        public static (T1, T2) Find<T1, T2>(this VisualElement value, string path1, string path2, bool throwException = true, bool silent = false) where T1 : VisualElement where T2 : VisualElement
        {
            return (value.Find<T1>(path1, throwException: throwException, silent: silent), value.Find<T2>(path2, throwException: throwException, silent: silent));
        }
        public static (T1, T2, T3) Find<T1, T2, T3>(this VisualElement value, string path1, string path2, string path3, bool throwException = true, bool silent = false) where T1 : VisualElement where T2 : VisualElement where T3 : VisualElement
        {
            return (value.Find<T1>(path1, throwException: throwException, silent: silent), value.Find<T2>(path2, throwException: throwException, silent: silent), value.Find<T3>(path3, throwException: throwException, silent: silent));
        }
        public static (T1, T2, T3, T4) Find<T1, T2, T3, T4>(this VisualElement value, string path1, string path2, string path3, string path4, bool throwException = true, bool silent = false) where T1 : VisualElement where T2 : VisualElement where T3 : VisualElement where T4 : VisualElement
        {
            return (value.Find<T1>(path1, throwException: throwException, silent: silent), value.Find<T2>(path2, throwException: throwException, silent: silent), value.Find<T3>(path3, throwException: throwException, silent: silent), value.Find<T4>(path4, throwException: throwException, silent: silent));
        }
        public static VisualElement Find(this VisualElement value, string path, string className = default, bool throwException = true, bool silent = true) => Find<VisualElement>(value, path, className, throwException, silent);

        public static T Clone<T>(this T value, VisualElement parent = default, int index = -1) where T : VisualElement
        {
            parent ??= value.parent;

            (VisualElement root, string pathToValue) = FindRoot(value, "");
            (UIDocument document, UxmlAttribute uxml, string path) metadata = rootMetadata[root];

            VisualElement documenRoot = new VisualElement();
            try
            {
                T elementClone;

                if (cloneMap.ContainsKey(value) && cloneMap[value] is string template && metadata.document.visualTreeAsset.templateDependencies.FirstOrDefault(x => x.name == template) is VisualTreeAsset treeAsset && treeAsset)
                {
                    treeAsset.CloneTree(documenRoot);
                    elementClone = (T)documenRoot[0];
                }
                else
                {
                    Debug.LogWarning($"[VisualElementMetadata] Cloning directly {value.GetType().Name}");

                    metadata.document.visualTreeAsset.CloneTree(documenRoot);
                    elementClone = documenRoot.Find(metadata.path).Find<T>(pathToValue);
                }

                elementClone.RemoveFromHierarchy();

                parent.Add(elementClone);
                if (value.parent == elementClone.parent) elementClone.PlaceBehind(value);
                if (index >= 0) elementClone.name = $"{value.name} {nameof(VisualElement)}:{index}";
                parent.MarkDirtyRepaint();

                if (elementClone is ISubElement subElement)
                {
                    Initialize(subElement, elementClone);
                    Rebuild(elementClone);
                }

                elementClone.MarginMe();

                return elementClone;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw new Exception($"Cannot clone {typeof(T).Name} [<color=yellow>{value.name}</color>]");
            }
            finally
            {
                documenRoot.RemoveFromHierarchy();
                documenRoot.Clear();
                documenRoot.MarkDirtyRepaint();
            }
        }
        public static VisualElement Clone(this VisualElement value, VisualElement parent = default, int index = -1) => Clone<VisualElement>(value, parent, index);

        public static T Replace<T>(this VisualElement value, VisualElement prefab) where T : VisualElement
        {
            VisualElement parent = value.parent;
            T elementClone = (T)prefab.Clone(parent);

            if (value.resolvedStyle.position == Position.Relative)
            {
                elementClone.style.position = value.style.position;
                elementClone.style.left = value.style.left;
                elementClone.style.top = value.style.top;
                elementClone.style.bottom = value.style.bottom;
                elementClone.style.right = value.style.right;
            }
            else
            {
                elementClone.style.alignItems = value.resolvedStyle.alignItems;
                elementClone.style.alignContent = value.resolvedStyle.alignContent;
                elementClone.style.justifyContent = value.resolvedStyle.justifyContent;
                elementClone.style.flexGrow = value.resolvedStyle.flexGrow;
                elementClone.style.flexShrink = value.resolvedStyle.flexShrink;
                elementClone.style.flexDirection = value.resolvedStyle.flexDirection;
                elementClone.style.flexWrap = value.resolvedStyle.flexWrap;

                elementClone.style.position = value.resolvedStyle.position;
                elementClone.style.left = value.resolvedStyle.left;
                elementClone.style.top = value.resolvedStyle.top;
                elementClone.style.bottom = value.resolvedStyle.bottom;
                elementClone.style.right = value.resolvedStyle.right;
            }

            elementClone.style.alignSelf = value.resolvedStyle.alignSelf;
            elementClone.name = value.name;

            elementClone.RemoveFromHierarchy();
            parent.Insert(parent.IndexOf(value), elementClone);
            parent.MarkDirtyRepaint();

            value.RemoveFromHierarchy();
            value.Clear();
            value.MarkDirtyRepaint();

            return elementClone;
        }
        public static VisualElement Replace(this VisualElement value, VisualElement prefab) => Replace<VisualElement>(value, prefab);

        public static void CopyStyleList(this VisualElement value, VisualElement source)
        {
            value.ClearClassList();
            foreach (string className in source.GetClasses()) value.AddToClassList(className);
        }
        public static void CopyResolvedStyle(this VisualElement value, VisualElement source, CopyStyleMask copyMask = CopyStyleMask.All)
        {
            IStyle style = value.style;
            IResolvedStyle valueResolvedStyle = value.resolvedStyle;
            IResolvedStyle sourceResolvedStyle = source.resolvedStyle;

            if (copyMask.HasFlag(CopyStyleMask.Position))
            {
                style.position = sourceResolvedStyle.position;
                style.left = sourceResolvedStyle.left;
                style.right = sourceResolvedStyle.right;
                style.top = sourceResolvedStyle.top;
                style.bottom = sourceResolvedStyle.bottom;
                style.scale = sourceResolvedStyle.scale;
                style.rotate = sourceResolvedStyle.rotate;
            }
            if (copyMask.HasFlag(CopyStyleMask.Size))
            {
                style.width = sourceResolvedStyle.width;
                style.height = sourceResolvedStyle.height;
            }
            if (copyMask.HasFlag(CopyStyleMask.Flex))
            {
                if (valueResolvedStyle.justifyContent != sourceResolvedStyle.justifyContent) style.justifyContent = sourceResolvedStyle.justifyContent;
                if (valueResolvedStyle.alignSelf != sourceResolvedStyle.alignSelf) style.alignSelf = sourceResolvedStyle.alignSelf;
                if (valueResolvedStyle.alignItems != sourceResolvedStyle.alignItems) style.alignItems = sourceResolvedStyle.alignItems;
                if (valueResolvedStyle.alignContent != sourceResolvedStyle.alignContent) style.alignContent = sourceResolvedStyle.alignContent;

                if (valueResolvedStyle.flexWrap != sourceResolvedStyle.flexWrap) style.flexWrap = sourceResolvedStyle.flexWrap;
                if (valueResolvedStyle.flexShrink != sourceResolvedStyle.flexShrink) style.flexShrink = sourceResolvedStyle.flexShrink;
                if (valueResolvedStyle.flexGrow != sourceResolvedStyle.flexGrow) style.flexGrow = sourceResolvedStyle.flexGrow;
                if (valueResolvedStyle.flexDirection != sourceResolvedStyle.flexDirection) style.flexDirection = sourceResolvedStyle.flexDirection;
                if (valueResolvedStyle.flexBasis != sourceResolvedStyle.flexBasis) style.flexBasis = sourceResolvedStyle.flexBasis.value;
            }
            if (copyMask.HasFlag(CopyStyleMask.Display))
            {
                if (valueResolvedStyle.display != sourceResolvedStyle.display) style.display = sourceResolvedStyle.display;
                if (valueResolvedStyle.opacity != sourceResolvedStyle.opacity) style.opacity = sourceResolvedStyle.opacity;
                if (valueResolvedStyle.visibility != sourceResolvedStyle.visibility) style.visibility = sourceResolvedStyle.visibility;
                if (valueResolvedStyle.unityBackgroundImageTintColor != sourceResolvedStyle.unityBackgroundImageTintColor) style.unityBackgroundImageTintColor = sourceResolvedStyle.unityBackgroundImageTintColor;
                if (valueResolvedStyle.unityBackgroundScaleMode != sourceResolvedStyle.unityBackgroundScaleMode) style.unityBackgroundScaleMode = sourceResolvedStyle.unityBackgroundScaleMode;
                if (valueResolvedStyle.backgroundImage != sourceResolvedStyle.backgroundImage) style.backgroundImage = sourceResolvedStyle.backgroundImage;
                if (valueResolvedStyle.backgroundColor != sourceResolvedStyle.backgroundColor) style.backgroundColor = sourceResolvedStyle.backgroundColor;
                if (valueResolvedStyle.color != sourceResolvedStyle.color) style.color = sourceResolvedStyle.color;
            }
            if (copyMask.HasFlag(CopyStyleMask.Padding))
            {
                if (valueResolvedStyle.paddingTop != sourceResolvedStyle.paddingTop) style.paddingTop = sourceResolvedStyle.paddingTop;
                if (valueResolvedStyle.paddingRight != sourceResolvedStyle.paddingRight) style.paddingRight = sourceResolvedStyle.paddingRight;
                if (valueResolvedStyle.paddingLeft != sourceResolvedStyle.paddingLeft) style.paddingLeft = sourceResolvedStyle.paddingLeft;
                if (valueResolvedStyle.paddingBottom != sourceResolvedStyle.paddingBottom) style.paddingBottom = sourceResolvedStyle.paddingBottom;
            }
            if (copyMask.HasFlag(CopyStyleMask.Margins))
            {
                if (valueResolvedStyle.marginTop != sourceResolvedStyle.marginTop) style.marginTop = sourceResolvedStyle.marginTop;
                if (valueResolvedStyle.marginRight != sourceResolvedStyle.marginRight) style.marginRight = sourceResolvedStyle.marginRight;
                if (valueResolvedStyle.marginLeft != sourceResolvedStyle.marginLeft) style.marginLeft = sourceResolvedStyle.marginLeft;
                if (valueResolvedStyle.marginBottom != sourceResolvedStyle.marginBottom) style.marginBottom = sourceResolvedStyle.marginBottom;
            }
            if (copyMask.HasFlag(CopyStyleMask.Borders))
            {
                if (valueResolvedStyle.borderTopLeftRadius != sourceResolvedStyle.borderTopLeftRadius) style.borderTopLeftRadius = sourceResolvedStyle.borderTopLeftRadius;
                if (valueResolvedStyle.borderTopColor != sourceResolvedStyle.borderTopColor) style.borderTopColor = sourceResolvedStyle.borderTopColor;
                if (valueResolvedStyle.borderRightWidth != sourceResolvedStyle.borderRightWidth) style.borderRightWidth = sourceResolvedStyle.borderRightWidth;
                if (valueResolvedStyle.borderRightColor != sourceResolvedStyle.borderRightColor) style.borderRightColor = sourceResolvedStyle.borderRightColor;
                if (valueResolvedStyle.borderLeftWidth != sourceResolvedStyle.borderLeftWidth) style.borderLeftWidth = sourceResolvedStyle.borderLeftWidth;
                if (valueResolvedStyle.borderLeftColor != sourceResolvedStyle.borderLeftColor) style.borderLeftColor = sourceResolvedStyle.borderLeftColor;
                if (valueResolvedStyle.borderTopRightRadius != sourceResolvedStyle.borderTopRightRadius) style.borderTopRightRadius = sourceResolvedStyle.borderTopRightRadius;
                if (valueResolvedStyle.borderBottomWidth != sourceResolvedStyle.borderBottomWidth) style.borderBottomWidth = sourceResolvedStyle.borderBottomWidth;
                if (valueResolvedStyle.borderBottomLeftRadius != sourceResolvedStyle.borderBottomLeftRadius) style.borderBottomLeftRadius = sourceResolvedStyle.borderBottomLeftRadius;
                if (valueResolvedStyle.borderBottomColor != sourceResolvedStyle.borderBottomColor) style.borderBottomColor = sourceResolvedStyle.borderBottomColor;
                if (valueResolvedStyle.borderBottomRightRadius != sourceResolvedStyle.borderBottomRightRadius) style.borderBottomRightRadius = sourceResolvedStyle.borderBottomRightRadius;
                if (valueResolvedStyle.borderTopWidth != sourceResolvedStyle.borderTopWidth) style.borderTopWidth = sourceResolvedStyle.borderTopWidth;
            }
            if (copyMask.HasFlag(CopyStyleMask.Slicing))
            {
                if (valueResolvedStyle.unitySliceTop != sourceResolvedStyle.unitySliceTop) style.unitySliceTop = sourceResolvedStyle.unitySliceTop;
                if (valueResolvedStyle.unitySliceRight != sourceResolvedStyle.unitySliceRight) style.unitySliceRight = sourceResolvedStyle.unitySliceRight;
                if (valueResolvedStyle.unitySliceLeft != sourceResolvedStyle.unitySliceLeft) style.unitySliceLeft = sourceResolvedStyle.unitySliceLeft;
                if (valueResolvedStyle.unitySliceBottom != sourceResolvedStyle.unitySliceBottom) style.unitySliceBottom = sourceResolvedStyle.unitySliceBottom;
            }
            if (copyMask.HasFlag(CopyStyleMask.Font))
            {
                if (valueResolvedStyle.whiteSpace != sourceResolvedStyle.whiteSpace) style.whiteSpace = sourceResolvedStyle.whiteSpace;
                if (valueResolvedStyle.wordSpacing != sourceResolvedStyle.wordSpacing) style.wordSpacing = sourceResolvedStyle.wordSpacing;
                if (valueResolvedStyle.letterSpacing != sourceResolvedStyle.letterSpacing) style.letterSpacing = sourceResolvedStyle.letterSpacing;
                if (valueResolvedStyle.textOverflow != sourceResolvedStyle.textOverflow) style.textOverflow = sourceResolvedStyle.textOverflow;
                if (valueResolvedStyle.fontSize != sourceResolvedStyle.fontSize) style.fontSize = sourceResolvedStyle.fontSize;
                if (valueResolvedStyle.unityFont != sourceResolvedStyle.unityFont) style.unityFont = new StyleFont() { value = sourceResolvedStyle.unityFont };
                if (valueResolvedStyle.unityFontDefinition != sourceResolvedStyle.unityFontDefinition) style.unityFontDefinition = sourceResolvedStyle.unityFontDefinition;
                if (valueResolvedStyle.unityParagraphSpacing != sourceResolvedStyle.unityParagraphSpacing) style.unityParagraphSpacing = sourceResolvedStyle.unityParagraphSpacing;
                if (valueResolvedStyle.unityTextAlign != sourceResolvedStyle.unityTextAlign) style.unityTextAlign = sourceResolvedStyle.unityTextAlign;
                if (valueResolvedStyle.unityTextOverflowPosition != sourceResolvedStyle.unityTextOverflowPosition) style.unityTextOverflowPosition = sourceResolvedStyle.unityTextOverflowPosition;
                if (valueResolvedStyle.unityTextOutlineWidth != sourceResolvedStyle.unityTextOutlineWidth) style.unityTextOutlineWidth = sourceResolvedStyle.unityTextOutlineWidth;
                if (valueResolvedStyle.unityTextOutlineColor != sourceResolvedStyle.unityTextOutlineColor) style.unityTextOutlineColor = sourceResolvedStyle.unityTextOutlineColor;
                if (valueResolvedStyle.unityFontStyleAndWeight != sourceResolvedStyle.unityFontStyleAndWeight) style.unityFontStyleAndWeight = sourceResolvedStyle.unityFontStyleAndWeight;
            }
        }
        public static void CopyStyle(this VisualElement value, VisualElement source, CopyStyleMask copyMask = CopyStyleMask.All)
        {
            IStyle valueStyle = value.style;
            IStyle sourceStyle = source.style;

            if (copyMask.HasFlag(CopyStyleMask.Position))
            {
                if (sourceStyle.position.keyword != StyleKeyword.Null && valueStyle.position != sourceStyle.position) valueStyle.position = sourceStyle.position;
                if (sourceStyle.top.keyword != StyleKeyword.Null && valueStyle.top != sourceStyle.top) valueStyle.top = sourceStyle.top;
                if (sourceStyle.bottom.keyword != StyleKeyword.Null && valueStyle.bottom != sourceStyle.bottom) valueStyle.bottom = sourceStyle.bottom;
                if (sourceStyle.left.keyword != StyleKeyword.Null && valueStyle.left != sourceStyle.left) valueStyle.left = sourceStyle.left;
                if (sourceStyle.right.keyword != StyleKeyword.Null && valueStyle.right != sourceStyle.right) valueStyle.right = sourceStyle.right;
                if (sourceStyle.translate.keyword != StyleKeyword.Null && valueStyle.translate != sourceStyle.translate) valueStyle.translate = valueStyle.translate = sourceStyle.translate;
                if (sourceStyle.rotate.keyword != StyleKeyword.Null && valueStyle.rotate != sourceStyle.rotate) valueStyle.rotate = sourceStyle.rotate;
                if (sourceStyle.scale.keyword != StyleKeyword.Null && valueStyle.scale != sourceStyle.scale) valueStyle.scale = sourceStyle.scale;
                if (sourceStyle.transitionTimingFunction.keyword != StyleKeyword.Null && valueStyle.transitionTimingFunction != sourceStyle.transitionTimingFunction) valueStyle.transitionTimingFunction = sourceStyle.transitionTimingFunction;
                if (sourceStyle.transitionProperty.keyword != StyleKeyword.Null && valueStyle.transitionProperty != sourceStyle.transitionProperty) valueStyle.transitionProperty = sourceStyle.transitionProperty;
                if (sourceStyle.transitionDuration.keyword != StyleKeyword.Null && valueStyle.transitionDuration != sourceStyle.transitionDuration) valueStyle.transitionDuration = sourceStyle.transitionDuration;
                if (sourceStyle.transitionDelay.keyword != StyleKeyword.Null && valueStyle.transitionDelay != sourceStyle.transitionDelay) valueStyle.transitionDelay = sourceStyle.transitionDelay;
                if (sourceStyle.transformOrigin.keyword != StyleKeyword.Null && valueStyle.transformOrigin != sourceStyle.transformOrigin) valueStyle.transformOrigin = sourceStyle.transformOrigin;
            }
            if (copyMask.HasFlag(CopyStyleMask.Size))
            {
                if (sourceStyle.width.keyword != StyleKeyword.Null && valueStyle.width != sourceStyle.width) valueStyle.width = sourceStyle.width;
                if (sourceStyle.minWidth.keyword != StyleKeyword.Null && valueStyle.minWidth != sourceStyle.minWidth) valueStyle.minWidth = sourceStyle.minWidth;
                if (sourceStyle.maxWidth.keyword != StyleKeyword.Null && valueStyle.maxWidth != sourceStyle.maxWidth) valueStyle.maxWidth = sourceStyle.maxWidth;
                if (sourceStyle.height.keyword != StyleKeyword.Null && valueStyle.height != sourceStyle.height) valueStyle.height = sourceStyle.height;
                if (sourceStyle.minHeight.keyword != StyleKeyword.Null && valueStyle.minHeight != sourceStyle.minHeight) valueStyle.minHeight = sourceStyle.minHeight;
                if (sourceStyle.maxHeight.keyword != StyleKeyword.Null && valueStyle.maxHeight != sourceStyle.maxHeight) valueStyle.maxHeight = sourceStyle.maxHeight;
            }
            if (copyMask.HasFlag(CopyStyleMask.Flex))
            {
                if (sourceStyle.alignSelf.keyword != StyleKeyword.Null && valueStyle.alignSelf != sourceStyle.alignSelf) valueStyle.alignSelf = sourceStyle.alignSelf;
                if (sourceStyle.alignContent.keyword != StyleKeyword.Null && valueStyle.alignContent != sourceStyle.alignContent) valueStyle.alignContent = sourceStyle.alignContent;
                if (sourceStyle.alignItems.keyword != StyleKeyword.Null && valueStyle.alignItems != sourceStyle.alignItems) valueStyle.alignItems = sourceStyle.alignItems;
                if (sourceStyle.justifyContent.keyword != StyleKeyword.Null && valueStyle.justifyContent != sourceStyle.justifyContent) valueStyle.justifyContent = sourceStyle.justifyContent;

                if (sourceStyle.flexDirection.keyword != StyleKeyword.Null && valueStyle.flexDirection != sourceStyle.flexDirection) valueStyle.flexDirection = sourceStyle.flexDirection;
                if (sourceStyle.flexWrap.keyword != StyleKeyword.Null && valueStyle.flexWrap != sourceStyle.flexWrap) valueStyle.flexWrap = sourceStyle.flexWrap;
                if (sourceStyle.flexBasis.keyword != StyleKeyword.Null && valueStyle.flexBasis != sourceStyle.flexBasis) valueStyle.flexBasis = sourceStyle.flexBasis.value;
                if (sourceStyle.flexShrink.keyword != StyleKeyword.Null && valueStyle.flexShrink != sourceStyle.flexShrink) valueStyle.flexShrink = sourceStyle.flexShrink;
                if (sourceStyle.flexGrow.keyword != StyleKeyword.Null && valueStyle.flexGrow != sourceStyle.flexGrow) valueStyle.flexGrow = sourceStyle.flexGrow;
            }
            if (copyMask.HasFlag(CopyStyleMask.Display))
            {
                if (sourceStyle.display.keyword != StyleKeyword.Null && valueStyle.display != sourceStyle.display) valueStyle.display = sourceStyle.display;
                if (sourceStyle.visibility.keyword != StyleKeyword.Null && valueStyle.visibility != sourceStyle.visibility) valueStyle.visibility = sourceStyle.visibility;
                if (sourceStyle.opacity.keyword != StyleKeyword.Null && valueStyle.opacity != sourceStyle.opacity) valueStyle.opacity = sourceStyle.opacity;
                if (sourceStyle.color.keyword != StyleKeyword.Null && valueStyle.color != sourceStyle.color) valueStyle.color = sourceStyle.color;
                if (sourceStyle.backgroundImage.keyword != StyleKeyword.Null && valueStyle.backgroundImage != sourceStyle.backgroundImage) valueStyle.backgroundImage = sourceStyle.backgroundImage;
                if (sourceStyle.backgroundColor.keyword != StyleKeyword.Null && valueStyle.backgroundColor != sourceStyle.backgroundColor) valueStyle.backgroundColor = sourceStyle.backgroundColor;
                if (sourceStyle.unityBackgroundImageTintColor.keyword != StyleKeyword.Null && valueStyle.unityBackgroundImageTintColor != sourceStyle.unityBackgroundImageTintColor) valueStyle.unityBackgroundImageTintColor = sourceStyle.unityBackgroundImageTintColor;
                if (sourceStyle.unityBackgroundScaleMode.keyword != StyleKeyword.Null && valueStyle.unityBackgroundScaleMode != sourceStyle.unityBackgroundScaleMode) valueStyle.unityBackgroundScaleMode = sourceStyle.unityBackgroundScaleMode;
            }
            if (copyMask.HasFlag(CopyStyleMask.Padding))
            {
                if (sourceStyle.paddingTop.keyword != StyleKeyword.Null && valueStyle.paddingTop != sourceStyle.paddingTop) valueStyle.paddingTop = sourceStyle.paddingTop;
                if (sourceStyle.paddingLeft.keyword != StyleKeyword.Null && valueStyle.paddingLeft != sourceStyle.paddingLeft) valueStyle.paddingLeft = sourceStyle.paddingLeft;
                if (sourceStyle.paddingRight.keyword != StyleKeyword.Null && valueStyle.paddingRight != sourceStyle.paddingRight) valueStyle.paddingRight = sourceStyle.paddingRight;
                if (sourceStyle.paddingBottom.keyword != StyleKeyword.Null && valueStyle.paddingBottom != sourceStyle.paddingBottom) valueStyle.paddingBottom = sourceStyle.paddingBottom;
            }
            if (copyMask.HasFlag(CopyStyleMask.Margins))
            {
                if (sourceStyle.marginTop.keyword != StyleKeyword.Null && valueStyle.marginTop != sourceStyle.marginTop) valueStyle.marginTop = sourceStyle.marginTop;
                if (sourceStyle.marginLeft.keyword != StyleKeyword.Null && valueStyle.marginLeft != sourceStyle.marginLeft) valueStyle.marginLeft = sourceStyle.marginLeft;
                if (sourceStyle.marginRight.keyword != StyleKeyword.Null && valueStyle.marginRight != sourceStyle.marginRight) valueStyle.marginRight = sourceStyle.marginRight;
                if (sourceStyle.marginBottom.keyword != StyleKeyword.Null && valueStyle.marginBottom != sourceStyle.marginBottom) valueStyle.marginBottom = sourceStyle.marginBottom;
            }
            if (copyMask.HasFlag(CopyStyleMask.Borders))
            {
                if (sourceStyle.borderTopColor.keyword != StyleKeyword.Null && valueStyle.borderTopColor != sourceStyle.borderTopColor) valueStyle.borderTopColor = sourceStyle.borderTopColor;
                if (sourceStyle.borderTopWidth.keyword != StyleKeyword.Null && valueStyle.borderTopWidth != sourceStyle.borderTopWidth) valueStyle.borderTopWidth = sourceStyle.borderTopWidth;
                if (sourceStyle.borderRightWidth.keyword != StyleKeyword.Null && valueStyle.borderRightWidth != sourceStyle.borderRightWidth) valueStyle.borderRightWidth = sourceStyle.borderRightWidth;
                if (sourceStyle.borderRightColor.keyword != StyleKeyword.Null && valueStyle.borderRightColor != sourceStyle.borderRightColor) valueStyle.borderRightColor = sourceStyle.borderRightColor;
                if (sourceStyle.borderLeftWidth.keyword != StyleKeyword.Null && valueStyle.borderLeftWidth != sourceStyle.borderLeftWidth) valueStyle.borderLeftWidth = sourceStyle.borderLeftWidth;
                if (sourceStyle.borderLeftColor.keyword != StyleKeyword.Null && valueStyle.borderLeftColor != sourceStyle.borderLeftColor) valueStyle.borderLeftColor = sourceStyle.borderLeftColor;
                if (sourceStyle.borderBottomWidth.keyword != StyleKeyword.Null && valueStyle.borderBottomWidth != sourceStyle.borderBottomWidth) valueStyle.borderBottomWidth = sourceStyle.borderBottomWidth;
                if (sourceStyle.borderBottomColor.keyword != StyleKeyword.Null && valueStyle.borderBottomColor != sourceStyle.borderBottomColor) valueStyle.borderBottomColor = sourceStyle.borderBottomColor;
                if (sourceStyle.borderTopLeftRadius.keyword != StyleKeyword.Null && valueStyle.borderTopLeftRadius != sourceStyle.borderTopLeftRadius) valueStyle.borderTopLeftRadius = sourceStyle.borderTopLeftRadius;
                if (sourceStyle.borderTopRightRadius.keyword != StyleKeyword.Null && valueStyle.borderTopRightRadius != sourceStyle.borderTopRightRadius) valueStyle.borderTopRightRadius = sourceStyle.borderTopRightRadius;
                if (sourceStyle.borderBottomLeftRadius.keyword != StyleKeyword.Null && valueStyle.borderBottomLeftRadius != sourceStyle.borderBottomLeftRadius) valueStyle.borderBottomLeftRadius = sourceStyle.borderBottomLeftRadius;
                if (sourceStyle.borderBottomRightRadius.keyword != StyleKeyword.Null && valueStyle.borderBottomRightRadius != sourceStyle.borderBottomRightRadius) valueStyle.borderBottomRightRadius = sourceStyle.borderBottomRightRadius;
            }
            if (copyMask.HasFlag(CopyStyleMask.Slicing))
            {
                if (sourceStyle.unitySliceLeft.keyword != StyleKeyword.Null && valueStyle.unitySliceLeft != sourceStyle.unitySliceLeft) valueStyle.unitySliceLeft = sourceStyle.unitySliceLeft;
                if (sourceStyle.unitySliceTop.keyword != StyleKeyword.Null && valueStyle.unitySliceTop != sourceStyle.unitySliceTop) valueStyle.unitySliceTop = sourceStyle.unitySliceTop;
                if (sourceStyle.unitySliceRight.keyword != StyleKeyword.Null && valueStyle.unitySliceRight != sourceStyle.unitySliceRight) valueStyle.unitySliceRight = sourceStyle.unitySliceRight;
                if (sourceStyle.unitySliceBottom.keyword != StyleKeyword.Null && valueStyle.unitySliceBottom != sourceStyle.unitySliceBottom) valueStyle.unitySliceBottom = sourceStyle.unitySliceBottom;
            }
            if (copyMask.HasFlag(CopyStyleMask.Font))
            {
                if (sourceStyle.fontSize.keyword != StyleKeyword.Null && valueStyle.fontSize != sourceStyle.fontSize) valueStyle.fontSize = sourceStyle.fontSize;
                if (sourceStyle.wordSpacing.keyword != StyleKeyword.Null && valueStyle.wordSpacing != sourceStyle.wordSpacing) valueStyle.wordSpacing = sourceStyle.wordSpacing;
                if (sourceStyle.whiteSpace.keyword != StyleKeyword.Null && valueStyle.whiteSpace != sourceStyle.whiteSpace) valueStyle.whiteSpace = sourceStyle.whiteSpace;
                if (sourceStyle.letterSpacing.keyword != StyleKeyword.Null && valueStyle.letterSpacing != sourceStyle.letterSpacing) valueStyle.letterSpacing = sourceStyle.letterSpacing;
                if (sourceStyle.textOverflow.keyword != StyleKeyword.Null && valueStyle.textOverflow != sourceStyle.textOverflow) valueStyle.textOverflow = sourceStyle.textOverflow;
                if (sourceStyle.unityFont.keyword != StyleKeyword.Null && valueStyle.unityFont != sourceStyle.unityFont) valueStyle.unityFont = sourceStyle.unityFont;
                if (sourceStyle.unityFontDefinition.keyword != StyleKeyword.Null && valueStyle.unityFontDefinition != sourceStyle.unityFontDefinition) valueStyle.unityFontDefinition = sourceStyle.unityFontDefinition;
                if (sourceStyle.unityTextOverflowPosition.keyword != StyleKeyword.Null && valueStyle.unityTextOverflowPosition != sourceStyle.unityTextOverflowPosition) valueStyle.unityTextOverflowPosition = sourceStyle.unityTextOverflowPosition;
                if (sourceStyle.unityTextOutlineWidth.keyword != StyleKeyword.Null && valueStyle.unityTextOutlineWidth != sourceStyle.unityTextOutlineWidth) valueStyle.unityTextOutlineWidth = sourceStyle.unityTextOutlineWidth;
                if (sourceStyle.unityTextOutlineColor.keyword != StyleKeyword.Null && valueStyle.unityTextOutlineColor != sourceStyle.unityTextOutlineColor) valueStyle.unityTextOutlineColor = sourceStyle.unityTextOutlineColor;
                if (sourceStyle.unityTextAlign.keyword != StyleKeyword.Null && valueStyle.unityTextAlign != sourceStyle.unityTextAlign) valueStyle.unityTextAlign = sourceStyle.unityTextAlign;
                if (sourceStyle.unityParagraphSpacing.keyword != StyleKeyword.Null && valueStyle.unityParagraphSpacing != sourceStyle.unityParagraphSpacing) valueStyle.unityParagraphSpacing = sourceStyle.unityParagraphSpacing;
                if (sourceStyle.unityFontStyleAndWeight.keyword != StyleKeyword.Null && valueStyle.unityFontStyleAndWeight != sourceStyle.unityFontStyleAndWeight) valueStyle.unityFontStyleAndWeight = sourceStyle.unityFontStyleAndWeight;
            }
        }

        public static float GetItemSpacing(this ICustomStyle style)
        {
            if (style.TryGetValue(new CustomStyleProperty<float>("--item-spacing"), out float spacing)) return spacing;
            else return float.NaN;
        }
        public static async void MarginMe(this VisualElement value)
        {
            await waitForEndOfFrame;

            VisualElement parent = value.parent;
            if (parent is null) return;

            float spacing = parent.customStyle.GetItemSpacing();
            if (spacing.Invalid()) return;

            int GetLines(VisualElement value, VisualElement parent, float spacing, bool horizontalDirection)
            {
                float valueSize = horizontalDirection ? value.resolvedStyle.width : value.resolvedStyle.height;
                float parentSize = horizontalDirection ? parent.resolvedStyle.width : parent.resolvedStyle.height;

                return (valueSize.Invalid() || valueSize == 0) ? parent.childCount : (int)(parentSize / ((2 * valueSize + (spacing.Invalid() ? 0 : spacing)) / 2));
            }
            int FindIndex(VisualElement value, IEnumerable<VisualElement> children)
            {
                int index = 0;
                foreach (VisualElement child in children)
                {
                    if (child == value) return index;
                    index++;
                }

                return -1;
            }

            IEnumerable<VisualElement> children = parent.Children().Where(x => x.resolvedStyle.display == DisplayStyle.Flex);

            bool horizontalDirection = parent.resolvedStyle.flexDirection == FlexDirection.Row;
            bool fixedSize = parent.resolvedStyle.flexWrap == Wrap.Wrap;

            if (fixedSize)
            {
                for (float i = 0; i < 1; i += Time.deltaTime)
                {
                    await Awaiters.NextFrame;
                    if (value.resolvedStyle.width > 0) break;
                }
            }
            int lines = fixedSize ? GetLines(value, parent, spacing, horizontalDirection) : children.Count();

            int index = FindIndex(value, children);
            float primaryMargin = lines > 0 ? (((index - 1) % lines != lines - 1) ? spacing : 0) : 0;
            float counterMargin = (index >= lines) ? spacing : 0;

            if (index == children.Count() - 1)
            {
                children.ElementAt(index).style.marginRight = 0;
                children.ElementAt(index).style.marginBottom = 0;
            }

            if (horizontalDirection)
            {
                if (index > 0) children.ElementAt(index - 1).style.marginRight = primaryMargin;
                if (index >= lines) children.ElementAt(index - lines).style.marginBottom = counterMargin;
            }
            else
            {
                if (index > 0) children.ElementAt(index - 1).style.marginBottom = primaryMargin;
                if (index >= lines) children.ElementAt(index - lines).style.marginRight = counterMargin;
            }
        }
        #endregion

        #region Support Methods
        static VisualElement FindRoot(VisualElement value)
        {
            if (rootMetadata.ContainsKey(value)) return value;
            else if (value.parent is not null) return FindRoot(value.parent);
            else return default;
        }
        static (VisualElement value, string path) FindRoot(VisualElement value, string path)
        {
            if (rootMetadata.ContainsKey(value)) return (value, path);
            if (value.parent is not null)
            {
                string name = value.name.Split($" {nameof(VisualElement)}:", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? value.name;
                return FindRoot(value.parent, path.NotNullOrEmpty() ? Path.Combine(name, path) : name);
            }
            throw new ArgumentException();
        }

        static void Initialize(object target, Type targetType, VisualElement targetRoot, bool throwException = false, bool silent = false)
        {
            VisualElement ResolveElement(FieldInfo field, QueryAttribute queryRoot, QueryAttribute query)
            {
                VisualElement Resolve()
                {
                    VisualElement Find(VisualElement root, QueryAttribute queryRoot, QueryAttribute query)
                    {
                        if (query is not null)
                        {
                            if (queryRoot is not null && queryRoot != query) return root.Find(queryRoot.Path, queryRoot.ClassName, throwException, silent)?.Find(query.Path, query.ClassName, throwException, silent);
                            return root.Find(query.Path, query.ClassName, throwException);
                        }
                        else throw new ArgumentNullException();
                    }

                    VisualElement value = Find(targetRoot, queryRoot, query);

                    if (query.ReplaceElementPath.NotNullOrEmpty())
                    {
                        if (value is not null) value = value.Replace(targetRoot.Find(query.ReplaceElementPath));
                        else
                        {
                            string name = Path.GetFileName(query.Path);
                            if (name == query.Path)
                            {
                                value = targetRoot.Find(query.ReplaceElementPath, default, throwException, silent)?.Clone(targetRoot);
                            }
                            else
                            {
                                string path = query.Path.Remove(query.Path.Length - name.Length - 1, name.Length + 1);
                                value = targetRoot.Find(query.ReplaceElementPath, default, throwException, silent)?.Clone(targetRoot.Find(path));
                            }
                            if (value is not null) value.name = name;
                        }
                    }
                    if (query.RebuildElementEvent.NotNullOrEmpty())
                    {
                        MethodInfo methodInfo = targetType.GetMethod(query.RebuildElementEvent, MethodsFlags);
                        value = (VisualElement)methodInfo.Invoke(target, new object[] { value });
                    }

                    if (value is null) return default;

                    Type valueType = value.GetType();
                    if (valueType != field.FieldType && valueType.IsAssignableFrom(field.FieldType) && field.FieldType != typeof(VisualElement))
                    {
                        if (throwException) throw new Exception($"Element `{value.name}` of type=[{value.GetType()}] cannot be inserterd into `{field.Name}` with type=[{field.FieldType}]");
                        else if (!silent) Debug.LogWarning($"Element `{value.name}` of type=[{value.GetType()}] cannot be inserterd into `{field.Name}` with type=[{field.FieldType}]");
                        return default;
                    }
                    field.SetValue(target, value);

                    return value;
                }
                void AddEvents(VisualElement value, QueryAttribute query)
                {
                    void AddEvent<TEventType>(VisualElement element, string name, TrickleDown trickleDown) where TEventType : EventBase<TEventType>, new()
                    {
                        if (name.NotNullOrEmpty())
                        {
                            MethodInfo methodInfo = targetType.GetMethod(name, MethodsFlags);
                            element.RegisterCallback((EventCallback<TEventType>)Delegate.CreateDelegate(typeof(EventCallback<TEventType>), target, methodInfo.Name, true), trickleDown);
                        }
                    }

                    AddEvent<MouseCaptureOutEvent>(value, query.MouseCaptureOutEvent, query.UseTrickleDown);
                    AddEvent<MouseCaptureEvent>(value, query.MouseCaptureEvent, query.UseTrickleDown);

                    AddEvent<ValidateCommandEvent>(value, query.ValidateCommandEvent, query.UseTrickleDown);
                    AddEvent<ExecuteCommandEvent>(value, query.ExecuteCommandEvent, query.UseTrickleDown);
#if UNITY_EDITOR
                    AddEvent<DragExitedEvent>(value, query.DragExitedEvent, query.UseTrickleDown);
                    AddEvent<DragUpdatedEvent>(value, query.DragUpdatedEvent, query.UseTrickleDown);
                    AddEvent<DragPerformEvent>(value, query.DragPerformEvent, query.UseTrickleDown);
                    AddEvent<DragEnterEvent>(value, query.DragEnterEvent, query.UseTrickleDown);
                    AddEvent<DragLeaveEvent>(value, query.DragLeaveEvent, query.UseTrickleDown);
#endif
                    AddEvent<FocusOutEvent>(value, query.FocusOutEvent, query.UseTrickleDown);
                    AddEvent<BlurEvent>(value, query.BlurEvent, query.UseTrickleDown);
                    AddEvent<FocusInEvent>(value, query.FocusInEvent, query.UseTrickleDown);
                    AddEvent<FocusEvent>(value, query.FocusEvent, query.UseTrickleDown);
                    AddEvent<InputEvent>(value, query.InputEvent, query.UseTrickleDown);
                    AddEvent<KeyDownEvent>(value, query.KeyDownEvent, query.UseTrickleDown);
                    AddEvent<KeyUpEvent>(value, query.KeyUpEvent, query.UseTrickleDown);
                    AddEvent<GeometryChangedEvent>(value, query.GeometryChangedEvent, query.UseTrickleDown);
                    AddEvent<PointerDownEvent>(value, query.PointerDownEvent, query.UseTrickleDown);
                    AddEvent<PointerUpEvent>(value, query.PointerUpEvent, query.UseTrickleDown);
                    AddEvent<PointerMoveEvent>(value, query.PointerMoveEvent, query.UseTrickleDown);
                    AddEvent<MouseDownEvent>(value, query.MouseDownEvent, query.UseTrickleDown);
                    AddEvent<MouseUpEvent>(value, query.MouseUpEvent, query.UseTrickleDown);
                    AddEvent<MouseMoveEvent>(value, query.MouseMoveEvent, query.UseTrickleDown);
                    AddEvent<ContextClickEvent>(value, query.ContextClickEvent, query.UseTrickleDown);
                    AddEvent<WheelEvent>(value, query.WheelEvent, query.UseTrickleDown);
                    AddEvent<MouseEnterEvent>(value, query.MouseEnterEvent, query.UseTrickleDown);
                    AddEvent<MouseLeaveEvent>(value, query.MouseLeaveEvent, query.UseTrickleDown);
                    AddEvent<MouseEnterWindowEvent>(value, query.MouseEnterWindowEvent, query.UseTrickleDown);
                    AddEvent<MouseLeaveWindowEvent>(value, query.MouseLeaveWindowEvent, query.UseTrickleDown);
                    AddEvent<MouseOverEvent>(value, query.MouseOverEvent, query.UseTrickleDown);
                    AddEvent<MouseOutEvent>(value, query.MouseOutEvent, query.UseTrickleDown);
                    AddEvent<ContextualMenuPopulateEvent>(value, query.ContextualMenuPopulateEvent, query.UseTrickleDown);
                    AddEvent<AttachToPanelEvent>(value, query.AttachToPanelEvent, query.UseTrickleDown);
                    AddEvent<DetachFromPanelEvent>(value, query.DetachFromPanelEvent, query.UseTrickleDown);
                    AddEvent<TooltipEvent>(value, query.TooltipEvent, query.UseTrickleDown);
                    AddEvent<IMGUIEvent>(value, query.IMGUIEvent, query.UseTrickleDown);
                }
                void AddCallbacks(VisualElement value, QueryAttribute query)
                {
                    EventCallback<TEventType> GetCallback<TEventType>(string name) where TEventType : EventBase<TEventType>, new()
                    {
                        MethodInfo methodInfo = targetType.GetMethod(name, MethodsFlags);
                        return (EventCallback<TEventType>)Delegate.CreateDelegate(typeof(EventCallback<TEventType>), target, methodInfo.Name, true);
                    }

                    if (query.ChangeEvent.NotNullOrEmpty() && value is TextField textField)
                        textField.RegisterValueChangedCallback(GetCallback<ChangeEvent<string>>(query.ChangeEvent));

                    if (query.ChangeEvent.NotNullOrEmpty() && value is Toggle toggleField)
                        toggleField.RegisterValueChangedCallback(GetCallback<ChangeEvent<bool>>(query.ChangeEvent));

                    if (query.ChangeEvent.NotNullOrEmpty() && value is SliderInt sliderIntField)
                        sliderIntField.RegisterValueChangedCallback(GetCallback<ChangeEvent<int>>(query.ChangeEvent));

                    if (query.ChangeEvent.NotNullOrEmpty() && value is INotifyValueChanged<float> notifyFloatValueChanged)
                        notifyFloatValueChanged.RegisterValueChangedCallback(GetCallback<ChangeEvent<float>>(query.ChangeEvent));
                }
                void AddClicked(VisualElement value, QueryAttribute query)
                {
                    if (query.Clicked.NotNullOrEmpty() && value is Button button)
                    {
                        MethodInfo methodInfo = targetType.GetMethod(query.Clicked, BindingFlags.NonPublic | BindingFlags.Instance);
                        button.clicked += (Action)Delegate.CreateDelegate(typeof(Action), target, methodInfo.Name, true);
                    }
                }
                void AddTemplate(VisualElement value, QueryAttribute query)
                {
                    if (query.Template.NotNullOrEmpty())
                    {
                        if (query.Template == "Hash")
                        {
                            cloneMap.Add(value, value.tooltip);
                            value.tooltip = default;
                        }
                        else cloneMap.Add(value, query.Template);
                    }
                }

                VisualElement element = Resolve();
                if (element is null) return default;

                AddEvents(element, query);
                AddCallbacks(element, query);
                AddClicked(element, query);
                AddTemplate(element, query);

                return element;
            }

            QueryAttribute queryRoot = default;
            foreach (FieldInfo field in targetType.GetFields(FieldsFlags))
            {
                QueryAttribute query = field.GetCustomAttribute<QueryAttribute>();
                if (query is null) continue;
                if (query.StartRoot) queryRoot = query;

                VisualElement element = ResolveElement(field, queryRoot, query);
                if (element is not null) OnInitializeElement?.Invoke(element, target, targetType, field, queryRoot, query);

                if (query.EndRoot) queryRoot = default;

                if (element is ISubElement subElement)
                {
                    Initialize(subElement, field.FieldType, element, throwException, silent);
                    subElement.OnInitialize();
                }
            }
        }
        #endregion
    }
}