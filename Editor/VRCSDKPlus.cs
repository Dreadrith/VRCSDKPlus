using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using AnimatorController = UnityEditor.Animations.AnimatorController;
using AnimatorControllerParameter = UnityEngine.AnimatorControllerParameter;
using AnimatorControllerParameterType = UnityEngine.AnimatorControllerParameterType;
using Object = UnityEngine.Object;

namespace DreadScripts.VRCSDKPlus
{
    internal sealed class VRCSDKPlus
    {
        private static bool initialized;
        private static GUIContent redWarnIcon;
        private static GUIContent yellowWarnIcon;
        private static GUIStyle centeredLabel => new GUIStyle(GUI.skin.label) {alignment = TextAnchor.MiddleCenter};
        private static readonly string[] allPlayables =
        {
            "Base",
            "Additive",
            "Gesture",
            "Action",
            "FX",
            "Sitting",
            "TPose",
            "IKPose"
        };
        
        private static VRCAvatarDescriptor avatar;
        private static VRCAvatarDescriptor[] validAvatars;
        private static AnimatorControllerParameter[] validParameters;

        private static string[] validPlayables;
        private static int[] validPlayableIndexes;

        private static void InitConstants()
        {
            if (initialized) return;
            redWarnIcon = new GUIContent(EditorGUIUtility.IconContent("CollabError"));
            //advancedPopupMethod = typeof(EditorGUI).GetMethod("AdvancedPopup", BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(Rect), typeof(int), typeof(string[]) }, null);
            yellowWarnIcon = new GUIContent(EditorGUIUtility.IconContent("d_console.warnicon.sml"));
            initialized = true;
        }

        private static void RefreshAvatar(System.Func<VRCAvatarDescriptor, bool> favoredAvatar = null)
        {
            RefreshAvatar(ref avatar, ref validAvatars, null, favoredAvatar);
            RefreshAvatarInfo();
        }

        private static void RefreshAvatarInfo()
        {
            RefreshValidParameters();
            RefreshValidPlayables();
        }
        private static void RefreshValidParameters()
        {
            if (!avatar)
            {
                validParameters = Array.Empty<AnimatorControllerParameter>();
                return;
            }
            List<AnimatorControllerParameter> validParams = new List<AnimatorControllerParameter>();
            foreach (var r in avatar.baseAnimationLayers.Concat(avatar.specialAnimationLayers).Select(p => p.animatorController).Concat(avatar.GetComponentsInChildren<Animator>(true).Select(a => a.runtimeAnimatorController)).Distinct())
            {
                if (!r) continue;

                AnimatorController c = AssetDatabase.LoadAssetAtPath<AnimatorController>(AssetDatabase.GetAssetPath(r));
                if (c) validParams.AddRange(c.parameters);
            }

            validParameters = validParams.Distinct().OrderBy(p => p.name).ToArray();
        }

        private static void RefreshValidPlayables()
        {
            if (!avatar)
            {
                validPlayables = Array.Empty<string>();
                validPlayableIndexes = Array.Empty<int>();
                return;
            }
            List<(string, int)> myPlayables = new List<(string, int)>();
            for (int i = 0; i < allPlayables.Length; i++)
            {
                int index = i == 0 ? i : i + 1;
                if (avatar.GetPlayableLayer((VRCAvatarDescriptor.AnimLayerType)index, out AnimatorController c))
                {
                    myPlayables.Add((allPlayables[i], index));
                }
            }

            validPlayables = new string[myPlayables.Count];
            validPlayableIndexes = new int[myPlayables.Count];
            for (int i = 0; i < myPlayables.Count; i++)
            {
                validPlayables[i] = myPlayables[i].Item1;
                validPlayableIndexes[i] = myPlayables[i].Item2;
            }
        }

        internal sealed class VRCParamsPlus : Editor
        {
            private static int _MAX_MEMORY_COST;
            private static int MAX_MEMORY_COST
            {
                get
                {
                    if (_MAX_MEMORY_COST == 0)
                    {
                        try
                        { _MAX_MEMORY_COST = (int) typeof(VRCExpressionParameters).GetField("MAX_PARAMETER_COST", BindingFlags.Static | BindingFlags.Public).GetValue(null); }
                        catch 
                        {
                            Debug.LogError("Failed to dynamically get MAX_PARAMETER_COST. Falling back to 256");
                            _MAX_MEMORY_COST = 256;
                        }
                    }

                    return _MAX_MEMORY_COST;
                }
            }

            private static readonly bool hasSyncingOption = typeof(VRCExpressionParameters.Parameter).GetField("networkSynced") != null;
            private static bool editorActive = true;
            private static bool canCleanup;
            private int currentCost;
            private string searchValue;

            private SerializedProperty parameterList;
            private ReorderableList parametersOrderList;

            private ParameterStatus[] _parameterStatus;

            private static VRCExpressionParameters mergeParams;
            
            public override void OnInspectorGUI()
            {
                EditorGUI.BeginChangeCheck();
                using (new GUILayout.HorizontalScope("helpbox"))
                    DrawAdvancedAvatarFull(ref avatar, validAvatars, RefreshValidParameters, false, false, false, "Active Avatar");

                canCleanup = false;
                serializedObject.Update();
                HandleParameterEvents();
                parametersOrderList.DoLayoutList();
                serializedObject.ApplyModifiedProperties();
                
                if (canCleanup)
                {
                    using (new GUILayout.HorizontalScope("helpbox"))
                    {
                        GUILayout.Label("Cleanup Invalid, Blank, and Duplicate Parameters");
                        if (ClickableButton("Cleanup"))
                        {
                            RefreshValidParameters();
                            parameterList.IterateArray((i, p) =>
                            {
                                var name = p.FindPropertyRelative("name").stringValue;
                                if (string.IsNullOrEmpty(name))
                                {
                                    GreenLog($"Deleted blank parameter at index {i}");
                                    parameterList.DeleteArrayElementAtIndex(i);
                                    return false;
                                }

                                if (avatar && validParameters.All(p2 => p2.name != name))
                                {
                                    GreenLog($"Deleted invalid parameter {name}");
                                    parameterList.DeleteArrayElementAtIndex(i);
                                    return false;
                                }

                                parameterList.IterateArray((j, p2) =>
                                {
                                    if (name == p2.FindPropertyRelative("name").stringValue)
                                    {
                                        GreenLog($"Deleted duplicate parameter {name}");
                                        parameterList.DeleteArrayElementAtIndex(j);
                                    }

                                    return false;
                                }, i);
                                
                                
                                return false;
                            });
                            serializedObject.ApplyModifiedProperties();
                            RefreshValidParameters();
                            GreenLog("Finished Cleanup!");
                        }
                    }
                }

                EditorGUI.BeginChangeCheck();
                using (new GUILayout.HorizontalScope("helpbox"))
                    mergeParams = (VRCExpressionParameters)EditorGUILayout.ObjectField("Merge Parameters", null, typeof(VRCExpressionParameters), true);
                if (EditorGUI.EndChangeCheck())
                {
                    if (mergeParams)
                    {
                        if (mergeParams.parameters != null)
                        {
                            VRCExpressionParameters myParams = (VRCExpressionParameters) target;
                            Undo.RecordObject(myParams, "Merge Parameters");
                            myParams.parameters = myParams.parameters.Concat(mergeParams.parameters.Select(p => 
                                new VRCExpressionParameters.Parameter()
                                {
                                    defaultValue = p.defaultValue,
                                    name = p.name,
                                    networkSynced = p.networkSynced,
                                    valueType = p.valueType
                                })).ToArray();
                            EditorUtility.SetDirty(myParams);
                        }
                        mergeParams = null;
                    }
                }

                CalculateTotalCost();
                try
                {
                    using (new EditorGUILayout.HorizontalScope("helpbox"))
                    {
                        GUILayout.FlexibleSpace();
                        using (new GUILayout.VerticalScope())
                        {
                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Label("Total Memory");
                                GUILayout.FlexibleSpace();

                            }

                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.FlexibleSpace();
                                GUILayout.Label($"{currentCost} / {MAX_MEMORY_COST}");
                                if (currentCost > MAX_MEMORY_COST)
                                    GUILayout.Label(redWarnIcon, GUILayout.Width(20));
                                GUILayout.FlexibleSpace();

                            }
                        }

                        GUILayout.FlexibleSpace();
                    }
                } catch{}
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    Link("Made By @Dreadrith ♡", "https://dreadrith.com/links");
                }

                if (EditorGUI.EndChangeCheck()) RefreshAllParameterStatus();
            }

            private void OnEnable()
            {
                InitConstants();
                RefreshAvatar(a => a.expressionParameters == target);

                parameterList = serializedObject.FindProperty("parameters");
                RefreshParametersOrderList();
                RefreshAllParameterStatus();
            }


            private static float guitest = 10;
            private void DrawElement(Rect rect, int index, bool active, bool focused)
            {
                if (!(index < parameterList.arraySize && index >= 0)) return;
                
                var screenRect = GUIUtility.GUIToScreenRect(rect);
                if (screenRect.y > Screen.currentResolution.height || screenRect.y + screenRect.height < 0) return;

                SerializedProperty parameter = parameterList.GetArrayElementAtIndex(index);
                SerializedProperty name = parameter.FindPropertyRelative("name");
                SerializedProperty valueType = parameter.FindPropertyRelative("valueType");
                SerializedProperty defaultValue = parameter.FindPropertyRelative("defaultValue");
                SerializedProperty saved = parameter.FindPropertyRelative("saved");
                SerializedProperty synced = hasSyncingOption ? parameter.FindPropertyRelative("networkSynced") : null;

                var status = _parameterStatus[index];
                bool parameterEmpty = status.parameterEmpty;
                bool parameterAddable = status.parameterAddable;
                bool parameterIsDuplicate = status.parameterIsDuplicate;
                bool hasWarning = status.hasWarning;
                string warnMsg = parameterEmpty ? "Blank Parameter" : parameterIsDuplicate ? "Duplicate Parameter! May cause issues!" : "Parameter not found in any playable controller of Active Avatar";
                AnimatorControllerParameter matchedParameter = status.matchedParameter;

                canCleanup |= hasWarning;

                #region Rects
                rect.y += 1;
                rect.height = 18;


                Rect UseNext( float width, bool fixedWidth = false, float position = -1, bool fixedPosition = false)
                {
                    Rect currentRect = rect;
                    currentRect.width = fixedWidth ? width : width * rect.width / 100;
                    currentRect.height = rect.height;
                    currentRect.x = position == -1 ? rect.x : fixedPosition ? position : rect.x + position * rect.width / 100;
                    currentRect.y = rect.y;
                    rect.x += currentRect.width;
                    return currentRect;
                }

                Rect UseEnd(ref Rect r, float width, bool fixedWidth = false, float positionOffset = -1, bool fixedPosition = false)
                {
                    Rect returnRect = r;
                    returnRect.width = fixedWidth ? width : width * r.width / 100;
                    float positionAdjust = positionOffset == -1 ? 0 : fixedPosition ? positionOffset : positionOffset * r.width / 100;
                    returnRect.x = r.x + r.width - returnRect.width - positionAdjust;
                    r.width -= returnRect.width + positionAdjust;
                    return returnRect;
                }
                
                Rect contextRect = rect;
                contextRect.x -= 20;
                contextRect.width = 20;
                
                Rect removeRect = UseEnd(ref rect, 32, true, 4, true);
                Rect syncedRect = hasSyncingOption ? UseEnd(ref rect, 18, true, 16f, true) : Rect.zero;
                Rect savedRect = UseEnd(ref rect, 18, true, hasSyncingOption ? 34f : 16, true);
                Rect defaultRect = UseEnd(ref rect, 85, true, 32, true);
                Rect typeRect = UseEnd(ref rect, 85, true, 12, true);
                Rect warnRect = UseEnd(ref rect, 18, true, 4, true);
                Rect addRect = hasWarning && parameterAddable ? UseEnd(ref rect, 55, true, 4, true) : Rect.zero;
                Rect dropdownRect = UseEnd(ref rect, 21, true, 1, true);
                dropdownRect.x -= 3;
                Rect nameRect = UseNext(100);

                //Rect removeRect = new Rect(rect.x + rect.width - 36, rect.y, 32, 18);
                //Rect syncedRect = new Rect(rect.x + rect.width - 60, rect.y, 14, 18);
                #endregion

                using (new EditorGUI.DisabledScope(!string.IsNullOrEmpty(searchValue) && !Regex.IsMatch(name.stringValue, $@"(?i){searchValue}")))
                {
                    //Hacky way to avoid proper UI Layout 
                    string parameterFieldName = $"namefield{index}";
                    
                    using (new EditorGUI.DisabledScope(validParameters.Length == 0))
                        if (GUI.Button(dropdownRect, GUIContent.none, EditorStyles.popup))
                        {

                            var filteredParameters = validParameters.Where(conParam => !parameterList.IterateArray((_, prop) => prop.FindPropertyRelative("name").stringValue == conParam.name)).ToArray();
                            if (filteredParameters.Any())
                            {
                                VRCSDKPlusToolbox.CustomDropdown<AnimatorControllerParameter> textDropdown = new VRCSDKPlusToolbox.CustomDropdown<AnimatorControllerParameter>(null, filteredParameters, item =>
                                {
                                    using (new GUILayout.HorizontalScope())
                                    {
                                        GUILayout.Label(item.value.name);
                                        GUILayout.Label(item.value.type.ToString(), VRCSDKPlusToolbox.Styles.Label.TypeLabel, GUILayout.ExpandWidth(false));
                                    }
                                }, (_, conParam) =>
                                {
                                    name.stringValue = conParam.name;
                                    name.serializedObject.ApplyModifiedProperties();
                                    RefreshAllParameterStatus();
                                });
                                textDropdown.EnableSearch((conParameter, search) => Regex.IsMatch(conParameter.name, $@"(?i){search}"));
                                textDropdown.Show(nameRect);
                            }
                        }

                    GUI.SetNextControlName(parameterFieldName);
                    EditorGUI.PropertyField(nameRect, name, GUIContent.none);
                    EditorGUI.PropertyField(typeRect, valueType, GUIContent.none);
                    EditorGUI.PropertyField(savedRect, saved, GUIContent.none);

                    GUI.Label(nameRect, matchedParameter != null ? $"({matchedParameter.type})" : "(?)", VRCSDKPlusToolbox.Styles.Label.RightPlaceHolder);

                    if (hasSyncingOption) EditorGUI.PropertyField(syncedRect, synced, GUIContent.none);

                    if (parameterAddable)
                    {
                        using (var change = new EditorGUI.ChangeCheckScope())
                        {
                            w_MakeRectLinkCursor(addRect);
                            int dummy = EditorGUI.IntPopup(addRect, -1, validPlayables, validPlayableIndexes);
                            if (change.changed)
                            {
                                var playable = (VRCAvatarDescriptor.AnimLayerType) dummy;
                                if (avatar.GetPlayableLayer(playable, out AnimatorController c))
                                {
                                    if (c.parameters.All(p => p.name != name.stringValue))
                                    {
                                        AnimatorControllerParameterType paramType;
                                        switch (valueType.enumValueIndex)
                                        {
                                            case 0:
                                                paramType = AnimatorControllerParameterType.Int;
                                                break;
                                            case 1:
                                                paramType = AnimatorControllerParameterType.Float;
                                                break;
                                            default:
                                            case 2:
                                                paramType = AnimatorControllerParameterType.Bool;
                                                break;
                                        }

                                        c.AddParameter(new AnimatorControllerParameter()
                                        {
                                            name = name.stringValue,
                                            type = paramType,
                                            defaultFloat = defaultValue.floatValue,
                                            defaultInt = (int) defaultValue.floatValue,
                                            defaultBool = defaultValue.floatValue > 0
                                        });

                                        GreenLog($"Added {paramType} {name.stringValue} to {playable} Playable Controller");
                                    }

                                    RefreshValidParameters();
                                }
                            }
                        }

                        addRect.x += 3;
                        GUI.Label(addRect, "Add");
                    }

                    if (hasWarning) GUI.Label(warnRect, new GUIContent(yellowWarnIcon) {tooltip = warnMsg});

                    switch (valueType.enumValueIndex)
                    {
                        case 2:
                            EditorGUI.BeginChangeCheck();
                            int dummy = EditorGUI.Popup(defaultRect, defaultValue.floatValue == 0 ? 0 : 1, new[] {"False", "True"});
                            if (EditorGUI.EndChangeCheck())
                                defaultValue.floatValue = dummy;
                            break;
                        default:
                            EditorGUI.PropertyField(defaultRect, defaultValue, GUIContent.none);
                            break;
                    }

                    w_MakeRectLinkCursor(removeRect);
                    if (GUI.Button(removeRect, VRCSDKPlusToolbox.GUIContent.Remove, VRCSDKPlusToolbox.Styles.Label.RemoveIcon))
                        DeleteParameter(index);
                }

                var e = Event.current;
                if (e.type == EventType.ContextClick && contextRect.Contains(e.mousePosition))
                {
                    e.Use();
                    var menu = new GenericMenu();
                    menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateParameter(index));
                    menu.AddSeparator(string.Empty);
                    menu.AddItem(new GUIContent("Delete"), false, () => DeleteParameter(index));
                    menu.ShowAsContext();
                }
            }
          

            private void DrawHeader(Rect rect)
            {
                #region Rects
                /*rect.y += 1;
                rect.height = 18;

                Rect baseRect = rect;

                Rect UseNext(float width, bool fixedWidth = false, float position = -1, bool fixedPosition = false)
                {
                    Rect currentRect = baseRect;
                    currentRect.width = fixedWidth ? width : width * baseRect.width / 100;
                    currentRect.height = baseRect.height;
                    currentRect.x = position == -1 ? baseRect.x : fixedPosition ? position : rect.x + position * baseRect.width / 100; ;
                    currentRect.y = baseRect.y;
                    baseRect.x += currentRect.width;
                    return currentRect;
                }

                Rect UseEnd(ref Rect r, float width, bool fixedWidth = false, float positionOffset = -1, bool fixedPosition = false)
                {
                    Rect returnRect = r;
                    returnRect.width = fixedWidth ? width : width * r.width / 100;
                    float positionAdjust = positionOffset == -1 ? 0 : fixedPosition ? positionOffset : positionOffset * r.width / 100;
                    returnRect.x = r.x + r.width - returnRect.width - positionAdjust;
                    r.width -= returnRect.width + positionAdjust;
                    return returnRect;
                }

                UseEnd(ref rect, 32, true, 4, true);
                Rect syncedRect = UseEnd(ref rect, 55, true);
                Rect savedRect = UseEnd(ref rect, 55, true);
                Rect defaultRect = UseEnd(ref rect, 60, true, 30, true);
                Rect typeRect = UseNext(16.66f);
                Rect nameRect = UseNext(rect.width * 0.4f, true);
                Rect searchIconRect = nameRect;
                searchIconRect.x += searchIconRect.width / 2 - 40;
                searchIconRect.width = 18;
                Rect searchRect = Rect.zero;
                Rect searchClearRect = Rect.zero;

                UseNext(canCleanup ? 12 : 26, true);
                UseNext(12, true);*/

                rect.y += 1;
                rect.height = 18;


                Rect UseNext(float width, bool fixedWidth = false, float position = -1, bool fixedPosition = false)
                {
                    Rect currentRect = rect;
                    currentRect.width = fixedWidth ? width : width * rect.width / 100;
                    currentRect.height = rect.height;
                    currentRect.x = position == -1 ? rect.x : fixedPosition ? position : rect.x + position * rect.width / 100;
                    currentRect.y = rect.y;
                    rect.x += currentRect.width;
                    return currentRect;
                }

                Rect UseEnd(ref Rect r, float width, bool fixedWidth = false, float positionOffset = -1, bool fixedPosition = false)
                {
                    Rect returnRect = r;
                    returnRect.width = fixedWidth ? width : width * r.width / 100;
                    float positionAdjust = positionOffset == -1 ? 0 : fixedPosition ? positionOffset : positionOffset * r.width / 100;
                    returnRect.x = r.x + r.width - returnRect.width - positionAdjust;
                    r.width -= returnRect.width + positionAdjust;
                    return returnRect;
                }

                UseEnd(ref rect, 32, true, 4, true);
                Rect syncedRect = hasSyncingOption ? UseEnd(ref rect, 54, true) : Rect.zero;
                Rect savedRect = UseEnd(ref rect, 54, true);
                Rect defaultRect = UseEnd(ref rect, 117, true);
                Rect typeRect = UseEnd(ref rect, 75, true);
                UseEnd(ref rect, 48, true);
                Rect nameRect = UseNext(100);

                //guitest = EditorGUILayout.FloatField(guitest);

                Rect searchIconRect = nameRect;
                searchIconRect.x += searchIconRect.width / 2 - 40;
                searchIconRect.width = 18;
                Rect searchRect = Rect.zero;
                Rect searchClearRect = Rect.zero;
                #endregion
                
                const string controlName = "VRCSDKParameterSearch";
                if (VRCSDKPlusToolbox.HasReceivedCommand(VRCSDKPlusToolbox.EventCommands.Find)) GUI.FocusControl(controlName);
                VRCSDKPlusToolbox.HandleTextFocusConfirmCommands(controlName, onCancel: () => searchValue = string.Empty);
                bool isFocused = GUI.GetNameOfFocusedControl() == controlName;
                bool isSearching = isFocused || !string.IsNullOrEmpty(searchValue);
                if (isSearching)
                {
                    searchRect = nameRect; searchRect.x += 14; searchRect.width -= 14;
                    searchClearRect = searchRect; searchClearRect.x += searchRect.width - 18; searchClearRect.y -= 1; searchClearRect.width = 16;
                }

                w_MakeRectLinkCursor(searchIconRect);
                if (GUI.Button(searchIconRect, VRCSDKPlusToolbox.GUIContent.Search, centeredLabel))
                    EditorGUI.FocusTextInControl(controlName);
                
                GUI.Label(nameRect, new GUIContent("Name","Name of the Parameter. This must match the name of the parameter that it is controlling in the playable layers. Case sensitive."), centeredLabel);


                w_MakeRectLinkCursor(searchClearRect);
                if (GUI.Button(searchClearRect, string.Empty, GUIStyle.none))
                {
                    searchValue = string.Empty;
                    if (isFocused) GUI.FocusControl(string.Empty);
                }
                GUI.SetNextControlName(controlName);
                searchValue = GUI.TextField(searchRect, searchValue, "SearchTextField");
                GUI.Button(searchClearRect, VRCSDKPlusToolbox.GUIContent.Clear, centeredLabel);
                GUI.Label(typeRect, new GUIContent("Type", "Type of the Parameter."), centeredLabel);
                GUI.Label(defaultRect, new GUIContent("Default", "The default/start value of this parameter."), centeredLabel);
                GUI.Label(savedRect, new GUIContent("Saved","Value will stay when loading avatar or changing worlds"), centeredLabel);
               
                if (hasSyncingOption) 
                    GUI.Label(syncedRect, new GUIContent("Synced", "Value will be sent over the network to remote users. This is needed if this value should be the same locally and remotely. Synced parameters count towards the total memory usage."), centeredLabel);

            }

            private void HandleParameterEvents()
            {
                if (!parametersOrderList.HasKeyboardControl()) return;
                if (!parametersOrderList.TryGetActiveIndex(out int index)) return;
                if (VRCSDKPlusToolbox.HasReceivedCommand(VRCSDKPlusToolbox.EventCommands.Duplicate))
                    DuplicateParameter(index);
                
                if (VRCSDKPlusToolbox.HasReceivedAnyDelete())
                    DeleteParameter(index);
            }

            
            #region Automated Methods
            [MenuItem("CONTEXT/VRCExpressionParameters/[SDK+] Toggle Editor", false, 899)]
            private static void ToggleEditor()
            {
                editorActive = !editorActive;

                var targetType = ExtendedGetType("VRCExpressionParameters");
                if (targetType == null)
                {
                    Debug.LogError("[VRCSDK+] VRCExpressionParameters was not found! Could not apply custom editor.");
                    return;
                }
                if (editorActive) OverrideEditor(targetType, typeof(VRCParamsPlus));
                else
                {
                    var expressionsEditor = ExtendedGetType("VRCExpressionParametersEditor");
                    if (expressionsEditor == null)
                    {
                        Debug.LogWarning("[VRCSDK+] VRCExpressionParametersEditor was not found! Could not apply custom editor");
                        return;
                    }
                    OverrideEditor(targetType, expressionsEditor);
                }

            }

            private void RefreshAllParameterStatus()
            {
                var expressionParameters = (VRCExpressionParameters)target;
                if (expressionParameters.parameters == null)
                {
                    expressionParameters.parameters = Array.Empty<VRCExpressionParameters.Parameter>();
                    EditorUtility.SetDirty(expressionParameters);
                }
                var parameters = expressionParameters.parameters;
                _parameterStatus = new ParameterStatus[parameters.Length];

                for (int index = 0; index < parameters.Length; index++)
                {
                    var exParameter = expressionParameters.parameters[index];
                    AnimatorControllerParameter matchedParameter = validParameters.FirstOrDefault(conParam => conParam.name == exParameter.name);
                    bool parameterEmpty = string.IsNullOrEmpty(exParameter.name);
                    bool parameterIsValid = matchedParameter != null;
                    bool parameterAddable = avatar && !parameterIsValid && !parameterEmpty;
                    bool parameterIsDuplicate = !parameterEmpty && expressionParameters.parameters.Where((p2, i) => index != i && exParameter.name == p2.name).Any(); ;
                    bool hasWarning = (avatar && !parameterIsValid) || parameterEmpty || parameterIsDuplicate;
                    _parameterStatus[index] = new ParameterStatus()
                    {
                        parameterEmpty = parameterEmpty,
                        parameterAddable = parameterAddable,
                        parameterIsDuplicate = parameterIsDuplicate,
                        hasWarning = hasWarning,
                        matchedParameter = matchedParameter
                    };
                }
            }

            private void CalculateTotalCost()
            {
                currentCost = 0;
                for (int i = 0; i < parameterList.arraySize; i++)
                {
                    SerializedProperty p = parameterList.GetArrayElementAtIndex(i);
                    SerializedProperty synced = p.FindPropertyRelative("networkSynced");
                    if (synced != null && !synced.boolValue) continue;
                    currentCost += p.FindPropertyRelative("valueType").enumValueIndex == 2 ? 1 : 8;
                }
            }

            private void RefreshParametersOrderList()
            {
                parametersOrderList = new ReorderableList(serializedObject, parameterList, true, true, true, false)
                {
                    drawElementCallback = DrawElement,
                    drawHeaderCallback = DrawHeader
                };
                parametersOrderList.onReorderCallback += _ => RefreshAllParameterStatus();
                parametersOrderList.onAddCallback = _ =>
                {
                    parameterList.InsertArrayElementAtIndex(parameterList.arraySize);
                    MakeParameterUnique(parameterList.arraySize - 1);
                };
            }

            private void DuplicateParameter(int index)
            {
                parameterList.InsertArrayElementAtIndex(index);
                MakeParameterUnique(index+1);
                parameterList.serializedObject.ApplyModifiedProperties();
                RefreshAllParameterStatus();
            }

            private void DeleteParameter(int index)
            {
                parameterList.DeleteArrayElementAtIndex(index);
                parameterList.serializedObject.ApplyModifiedProperties();
                RefreshAllParameterStatus();
            }
            private void MakeParameterUnique(int index)
            {
                var newElement = parameterList.GetArrayElementAtIndex(index);
                var nameProp = newElement.FindPropertyRelative("name");
                nameProp.stringValue = VRCSDKPlusToolbox.GenerateUniqueString(nameProp.stringValue, newName =>
                {
                    for (int i = 0; i < parameterList.arraySize; i++)
                    {
                        if (i == index) continue;
                        var p = parameterList.GetArrayElementAtIndex(i);
                        if (p.FindPropertyRelative("name").stringValue == newName) return false;
                    }
                    return true;
                });
            }

            #endregion

            private struct ParameterStatus
            {
                internal bool parameterEmpty;
                internal bool parameterAddable;
                internal bool parameterIsDuplicate;
                internal bool hasWarning;
                internal AnimatorControllerParameter matchedParameter;
            }

        }

        internal sealed class VRCMenuPlus : Editor, IHasCustomMenu
        {
            private static bool editorActive = true;
            private static VRCAvatarDescriptor _avatar;
            private VRCAvatarDescriptor[] _validAvatars;
            private ReorderableList _controlsList;

            private static readonly LinkedList<VRCExpressionsMenu> _menuHistory = new LinkedList<VRCExpressionsMenu>();
            private static LinkedListNode<VRCExpressionsMenu> _currentNode;
            private static VRCExpressionsMenu _lastMenu;

            private static VRCExpressionsMenu moveSourceMenu;
            private static VRCExpressionsMenu.Control moveTargetControl;
            private static bool isMoving;

            #region Initialization
            private void ReInitializeAll()
            {
                CheckAvatar();
                CheckMenu();
                InitializeList();
            }

            private void CheckAvatar()
            {
                _validAvatars = FindObjectsOfType<VRCAvatarDescriptor>();
                if (_validAvatars.Length == 0) _avatar = null;
                else if (!_avatar) _avatar = _validAvatars[0];
            }

            private void CheckMenu()
            {
                var currentMenu = target as VRCExpressionsMenu;
                if (!currentMenu || currentMenu == _lastMenu) return;

                if (_currentNode != null && _menuHistory.Last != _currentNode)
                {
                    var node = _currentNode.Next;
                    while (node != null)
                    {
                        var nextNode = node.Next;
                        _menuHistory.Remove(node);
                        node = nextNode;
                    }
                }

                _lastMenu = currentMenu;
                _currentNode = _menuHistory.AddLast(currentMenu);
            }

            private void InitializeList()
            {
                var l = serializedObject.FindProperty("controls");
                _controlsList = new ReorderableList(serializedObject, l, true, true, true, false);
                _controlsList.onCanAddCallback += reorderableList => reorderableList.count < 8;
                _controlsList.onAddCallback = _ =>
                {
                    var controlsProp = _controlsList.serializedProperty;
                    var index = controlsProp.arraySize++;
                    _controlsList.index = index;

                    var c = controlsProp.GetArrayElementAtIndex(index);
                    c.FindPropertyRelative("name").stringValue = "New Control";
                    c.FindPropertyRelative("icon").objectReferenceValue = null;
                    c.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "";
                    c.FindPropertyRelative("type").enumValueIndex = 1;
                    c.FindPropertyRelative("subMenu").objectReferenceValue = null;
                    c.FindPropertyRelative("labels").ClearArray();
                    c.FindPropertyRelative("subParameters").ClearArray();
                    c.FindPropertyRelative("value").floatValue = 1;
                };
                _controlsList.drawHeaderCallback = rect =>
                {
                    if (isMoving && Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Escape)
                    {
                        isMoving = false;
                        Repaint();
                    }
                    EditorGUI.LabelField(rect, $"Controls ({_controlsList.count} / 8)");

                    // Draw copy, paste, duplicate, and move buttons
                    #region Rects
                    var copyRect = new Rect(
                        rect.x + rect.width - rect.height - ((rect.height + VRCSDKPlusToolbox.Styles.Padding) * 3),
                        rect.y,
                        rect.height,
                        rect.height);

                    var pasteRect = new Rect(
                        copyRect.x + copyRect.width + VRCSDKPlusToolbox.Styles.Padding,
                        copyRect.y,
                        copyRect.height,
                        copyRect.height);

                    var duplicateRect = new Rect(
                        pasteRect.x + pasteRect.width + VRCSDKPlusToolbox.Styles.Padding,
                        pasteRect.y,
                        pasteRect.height,
                        pasteRect.height);

                    var moveRect = new Rect(
                        duplicateRect.x + duplicateRect.width + VRCSDKPlusToolbox.Styles.Padding,
                        duplicateRect.y,
                        duplicateRect.height,
                        duplicateRect.height);
                    
                    #endregion

                    bool isFull = _controlsList.count >= 8;
                    bool isEmpty = _controlsList.count == 0;
                    bool hasIndex = _controlsList.TryGetActiveIndex(out int index);
                    bool hasFocus = _controlsList.HasKeyboardControl();
                    if (!hasIndex) index = _controlsList.count;
                    using (new EditorGUI.DisabledScope(isEmpty || !hasFocus || !hasIndex))
                    {
                        #region Copy

                        w_MakeRectLinkCursor(copyRect);
                        if (GUI.Button(copyRect, VRCSDKPlusToolbox.GUIContent.Copy, GUI.skin.label))
                            CopyControl(index);
                                

                        #endregion

                        // This section was also created entirely by GitHub Copilot :3

                        #region Duplicate

                        using (new EditorGUI.DisabledScope(isFull))
                        {
                            w_MakeRectLinkCursor(duplicateRect);
                            if (GUI.Button(duplicateRect, isFull ? new GUIContent(VRCSDKPlusToolbox.GUIContent.Duplicate) { tooltip = VRCSDKPlusToolbox.GUIContent.MenuFullTooltip } : VRCSDKPlusToolbox.GUIContent.Duplicate, GUI.skin.label))
                                DuplicateControl(index);
                        }

                        #endregion
                    }

                    #region Paste
                    using (new EditorGUI.DisabledScope(!CanPasteControl()))
                    {
                        w_MakeRectLinkCursor(pasteRect);
                        if (GUI.Button(pasteRect, VRCSDKPlusToolbox.GUIContent.Paste, GUI.skin.label))
                        {
                            var menu = new GenericMenu();
                            menu.AddItem(new GUIContent("Paste values"), false, isEmpty || !hasFocus ? (GenericMenu.MenuFunction)null : () => PasteControl(index, false));
                            menu.AddItem(
                                new GUIContent("Insert as new"),
                                false,
                                isFull ? (GenericMenu.MenuFunction)null : () => PasteControl(index, true)
                            );
                            menu.ShowAsContext();
                        }
                    }
                    #endregion


                    #region Move
                    using (new EditorGUI.DisabledScope((isMoving && isFull) || (!isMoving && (!hasFocus || isEmpty))))
                    {
                        w_MakeRectLinkCursor(moveRect);
                        if (GUI.Button(moveRect, isMoving ? isFull ? new GUIContent(VRCSDKPlusToolbox.GUIContent.Place) {tooltip = VRCSDKPlusToolbox.GUIContent.MenuFullTooltip} : VRCSDKPlusToolbox.GUIContent.Place : VRCSDKPlusToolbox.GUIContent.Move, GUI.skin.label))
                        {
                            if (!isMoving) MoveControl(index);
                            else PlaceControl(index);
                        }
                    }

                    #endregion


                };
                _controlsList.drawElementCallback = (rect2, index, _, focused) =>
                {
                    if (!(index < l.arraySize && index >= 0)) return;
                    var controlProp = l.GetArrayElementAtIndex(index);
                    var controlType = controlProp.FindPropertyRelative("type").ToControlType();
                    Rect removeRect = new Rect(rect2.width + 3, rect2.y + 1, 32, 18);
                    rect2.width -= 48;
                    // Draw control type
                    EditorGUI.LabelField(rect2, controlType.ToString(), focused
                            ? VRCSDKPlusToolbox.Styles.Label.TypeFocused
                            : VRCSDKPlusToolbox.Styles.Label.Type);

                    // Draw control name
                    var nameGuiContent = new GUIContent(controlProp.FindPropertyRelative("name").stringValue);
                    bool emptyName = string.IsNullOrEmpty(nameGuiContent.text);
                    if (emptyName) nameGuiContent.text = "[Unnamed]";

                    var nameRect = new Rect(rect2.x, rect2.y, VRCSDKPlusToolbox.Styles.Label.RichText.CalcSize(nameGuiContent).x, rect2.height);

                    EditorGUI.LabelField(nameRect,
                        new GUIContent(nameGuiContent),
                        emptyName ? VRCSDKPlusToolbox.Styles.Label.PlaceHolder : VRCSDKPlusToolbox.Styles.Label.RichText);

                    w_MakeRectLinkCursor(removeRect);
                    if (GUI.Button(removeRect, VRCSDKPlusToolbox.GUIContent.Remove, VRCSDKPlusToolbox.Styles.Label.RemoveIcon))
                        DeleteControl(index);

                    var e = Event.current;
                    
                    if (controlType == VRCExpressionsMenu.Control.ControlType.SubMenu)
                    {
                        if (e.clickCount == 2 && e.type == EventType.MouseDown && rect2.Contains(e.mousePosition))
                        {
                            var sm = controlProp.FindPropertyRelative("subMenu").objectReferenceValue;
                            if (sm) Selection.activeObject = sm;
                            e.Use();
                        }
                    }

                    if (e.type == EventType.ContextClick && rect2.Contains(e.mousePosition))
                    {
                        e.Use();
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Cut"), false, () => MoveControl(index));
                        menu.AddItem(new GUIContent("Copy"), false, () => CopyControl(index));
                        if (!CanPasteControl()) menu.AddDisabledItem(new GUIContent("Paste"));
                        else
                        {
                            menu.AddItem(new GUIContent("Paste/Values"), false, () =>  PasteControl(index, false));
                            menu.AddItem(new GUIContent("Paste/As New"), false, () =>  PasteControl(index, true));
                        }
                        menu.AddSeparator(string.Empty);
                        menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateControl(index));
                        menu.AddItem(new GUIContent("Delete"), false, () => DeleteControl(index));
                        menu.ShowAsContext();
                    }
                    
                };
            }

            private VRCExpressionParameters.Parameter FetchParameter(string name)
            {
                if (!_avatar || !_avatar.expressionParameters) return null;
                var par = _avatar.expressionParameters;
                return par.parameters?.FirstOrDefault(p => p.name == name);
            }
            #endregion

            public override void OnInspectorGUI()
            {
                serializedObject.Update();
                HandleControlEvents();
                DrawHistory();
                DrawHead();
                DrawBody();
                DrawFooter();
                serializedObject.ApplyModifiedProperties();

            }

            private void OnEnable() => ReInitializeAll();

            private void DrawHistory()
            {
                using (new GUILayout.HorizontalScope("helpbox"))
                {
                    void CheckHistory()
                    {
                        for (LinkedListNode<VRCExpressionsMenu> node = _menuHistory.First; node != null;)
                        {
                            LinkedListNode<VRCExpressionsMenu> next = node.Next;
                            if (node.Value == null) _menuHistory.Remove(node);
                            node = next;
                        }
                    }

                    void SetCurrentNode(LinkedListNode<VRCExpressionsMenu> node)
                    {
                        if (node.Value == null) return;
                        _currentNode = node;
                        Selection.activeObject = _lastMenu = _currentNode.Value;
                    }

                    using (new EditorGUI.DisabledScope(_currentNode.Previous == null))
                    {
                        using (new EditorGUI.DisabledScope(_currentNode.Previous == null))
                        {
                            if (ClickableButton("<<", GUILayout.ExpandWidth(false)))
                            {
                                CheckHistory();
                                SetCurrentNode(_menuHistory.First);
                            }

                            if (ClickableButton("<", GUILayout.ExpandWidth(false)))
                            {
                                CheckHistory();
                                SetCurrentNode(_currentNode.Previous);
                            }
                        }
                    }

                    if (ClickableButton(_lastMenu.name, VRCSDKPlusToolbox.Styles.Label.Centered, GUILayout.ExpandWidth(true)))
                        EditorGUIUtility.PingObject(_lastMenu);

                    using (new EditorGUI.DisabledScope(_currentNode.Next == null))
                    {
                        if (ClickableButton(">", GUILayout.ExpandWidth(false)))
                        {
                            CheckHistory();
                            SetCurrentNode(_currentNode.Next);
                        }

                        if (ClickableButton(">>", GUILayout.ExpandWidth(false)))
                        {
                            CheckHistory();
                            SetCurrentNode(_menuHistory.Last);
                        }
                    }

                }
            }
            private void DrawHead()
            {
                #region Avatar Selector

                // Generate name string array
                var targetsAsString = _validAvatars.Select(t => t.gameObject.name).ToArray();

                // Draw selection
                using (new EditorGUI.DisabledScope(_validAvatars.Length <= 1))
                {
                    using (new VRCSDKPlusToolbox.Container.Horizontal())
                    {
                        var content = new GUIContent("Active Avatar", "The auto-fill and warnings will be based on this avatar's expression parameters");
                        if (_validAvatars.Length >= 1)
                        {
                            using (var change = new EditorGUI.ChangeCheckScope())
                            {
                                var targetIndex = EditorGUILayout.Popup(
                                    content,
                                    _validAvatars.FindIndex(_avatar),
                                    targetsAsString);

                                if (targetIndex == -1)
                                    ReInitializeAll();
                                else if (change.changed)
                                {
                                    _avatar = _validAvatars[targetIndex];
                                    ReInitializeAll();
                                }
                            }
                        }
                        else EditorGUILayout.LabelField(content, new GUIContent("No Avatar Descriptors found"), VRCSDKPlusToolbox.Styles.Label.LabelDropdown);

                        if (_avatar == null || !_avatar.expressionParameters)
                            GUILayout.Label(new GUIContent(VRCSDKPlusToolbox.GUIContent.Error) { tooltip = VRCSDKPlusToolbox.GUIContent.MissingParametersTooltip }, GUILayout.Width(18));
                    }
                }

                #endregion
            }
            void DrawBody()
            {

                if (_controlsList == null)
                    InitializeList();

                if (_controlsList.index == -1 && _controlsList.count != 0)
                    _controlsList.index = 0;
                
                _controlsList.DoLayoutList();
                if (_controlsList.count == 0)
                    _controlsList.index = -1;

                // EditorGUILayout.Separator();

                var control = _controlsList.index < 0 || _controlsList.index >= _controlsList.count ? null : _controlsList.serializedProperty.GetArrayElementAtIndex(_controlsList.index);
                var expressionParameters = _avatar == null ? null : _avatar.expressionParameters;

                if (VRCSDKPlusToolbox.Preferences.CompactMode)
                    ControlRenderer.DrawControlCompact(control, expressionParameters);
                else
                    ControlRenderer.DrawControl(control, expressionParameters);

            }

            void DrawFooter()
            {
                using (new GUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("Editor by", VRCSDKPlusToolbox.Styles.Label.Watermark);
                    Link("@fox_score","https://github.com/foxscore");
                    GUILayout.Label("&", VRCSDKPlusToolbox.Styles.Label.Watermark);
                    Link("@Dreadrith", "https://dreadrith.com/links");
                }
            }

            private void HandleControlEvents()
            {
                if (!_controlsList.HasKeyboardControl()) return;
                if (!_controlsList.TryGetActiveIndex(out int index)) return;
                bool fullMenu = _controlsList.count >= 8;

                bool WarnIfFull()
                {
                    if (fullMenu)
                    {
                        Debug.LogWarning(VRCSDKPlusToolbox.GUIContent.MenuFullTooltip);
                        return true;
                    }

                    return false;
                }
                
                if (VRCSDKPlusToolbox.HasReceivedAnyDelete())
                    DeleteControl(index);
                
                if (VRCSDKPlusToolbox.HasReceivedCommand(VRCSDKPlusToolbox.EventCommands.Duplicate))
                    if (!WarnIfFull()) DuplicateControl(index);
                
                if (VRCSDKPlusToolbox.HasReceivedCommand(VRCSDKPlusToolbox.EventCommands.Copy))
                    CopyControl(index);
                
                if (VRCSDKPlusToolbox.HasReceivedCommand(VRCSDKPlusToolbox.EventCommands.Cut))
                    MoveControl(index);
                
                if (VRCSDKPlusToolbox.HasReceivedCommand(VRCSDKPlusToolbox.EventCommands.Paste))
                    if (isMoving && !WarnIfFull()) PlaceControl(index);
                    else if (CanPasteControl() && !WarnIfFull()) PasteControl(index, true);
            }
            
            #region Control Methods
            private void CopyControl(int index)
            {
                EditorGUIUtility.systemCopyBuffer =
                    VRCSDKPlusToolbox.Strings.ClipboardPrefixControl +
                    JsonUtility.ToJson(((VRCExpressionsMenu)target).controls[index]);
            }
            
            private static bool CanPasteControl() => EditorGUIUtility.systemCopyBuffer.StartsWith(VRCSDKPlusToolbox.Strings.ClipboardPrefixControl);
            private void PasteControl(int index, bool asNew)
            {
                if (!CanPasteControl()) return;
                if (!asNew)
                {
                    var control = JsonUtility.FromJson<VRCExpressionsMenu.Control>(
                        EditorGUIUtility.systemCopyBuffer.Substring(VRCSDKPlusToolbox.Strings.ClipboardPrefixControl.Length));

                    Undo.RecordObject(target, "Paste control values");
                    _lastMenu.controls[index] = control;
                    EditorUtility.SetDirty(_lastMenu);
                }
                else
                {
                    var newControl = JsonUtility.FromJson<VRCExpressionsMenu.Control>(
                        EditorGUIUtility.systemCopyBuffer.Substring(VRCSDKPlusToolbox.Strings.ClipboardPrefixControl.Length));

                    Undo.RecordObject(target, "Insert control as new");
                    if (_lastMenu.controls.Count <= 0)
                    {
                        _lastMenu.controls.Add(newControl);
                        _controlsList.index = 0;
                    }
                    else
                    {
                        var insertIndex = index + 1;
                        if (insertIndex < 0) insertIndex = 0;
                        _lastMenu.controls.Insert(insertIndex, newControl);
                        _controlsList.index = insertIndex;
                    }
                    EditorUtility.SetDirty(_lastMenu);
                }
            }

            private void DuplicateControl(int index)
            {
                var controlsProp = _controlsList.serializedProperty;
                controlsProp.InsertArrayElementAtIndex(index);
                _controlsList.index = index + 1;

                var newElement = controlsProp.GetArrayElementAtIndex(index+1);
                var lastName = newElement.FindPropertyRelative("name").stringValue;
                newElement.FindPropertyRelative("name").stringValue = VRCSDKPlusToolbox.GenerateUniqueString(lastName, newName => newName != lastName, false);

                if (Event.current.shift) return;
                var menuParameter = newElement.FindPropertyRelative("parameter");
                if (menuParameter == null) return;
                var parName = menuParameter.FindPropertyRelative("name").stringValue;
                if (string.IsNullOrEmpty(parName)) return;
                var matchedParameter = FetchParameter(parName);
                if (matchedParameter == null) return;
                var controlType = newElement.FindPropertyRelative("type").ToControlType();
                if (controlType != VRCExpressionsMenu.Control.ControlType.Button && controlType != VRCExpressionsMenu.Control.ControlType.Toggle) return;

                if (matchedParameter.valueType == VRCExpressionParameters.ValueType.Bool)
                {
                    menuParameter.FindPropertyRelative("name").stringValue = VRCSDKPlusToolbox.GenerateUniqueString(parName, s => s != parName, false);
                }
                else
                {
                    var controlValueProp = newElement.FindPropertyRelative("value");
                    if (Mathf.RoundToInt(controlValueProp.floatValue) == controlValueProp.floatValue)
                        controlValueProp.floatValue++;
                }
            }

            private void DeleteControl(int index)
            {
                if (_controlsList.index == index) _controlsList.index--;
                _controlsList.serializedProperty.DeleteArrayElementAtIndex(index);
            }

            private void MoveControl(int index)
            {
                isMoving = true;
                moveSourceMenu = _lastMenu;
                moveTargetControl = _lastMenu.controls[index];
            }

            private void PlaceControl(int index)
            {
                isMoving = false;
                if (moveSourceMenu && moveTargetControl != null)
                {
                    Undo.RecordObject(target, "Move control");
                    Undo.RecordObject(moveSourceMenu, "Move control");

                    if (_lastMenu.controls.Count <= 0)
                        _lastMenu.controls.Add(moveTargetControl);
                    else 
                    {
                        var insertIndex = index + 1;
                        if (insertIndex < 0) insertIndex = 0;
                        _lastMenu.controls.Insert(insertIndex, moveTargetControl);
                        moveSourceMenu.controls.Remove(moveTargetControl);
                    }

                    EditorUtility.SetDirty(moveSourceMenu);
                    EditorUtility.SetDirty(target);

                    if (Event.current.shift) Selection.activeObject = moveSourceMenu;
                }
            }

            #endregion

            public void AddItemsToMenu(GenericMenu menu) => menu.AddItem(new GUIContent("Compact Mode"), VRCSDKPlusToolbox.Preferences.CompactMode, ToggleCompactMode);
            private static void ToggleCompactMode() => VRCSDKPlusToolbox.Preferences.CompactMode = !VRCSDKPlusToolbox.Preferences.CompactMode;

            [MenuItem("CONTEXT/VRCExpressionsMenu/[SDK+] Toggle Editor", false, 899)]
            private static void ToggleEditor()
            {
                editorActive = !editorActive;
                var targetType = ExtendedGetType("VRCExpressionsMenu");
                if (targetType == null)
                {
                    Debug.LogError("[VRCSDK+] VRCExpressionsMenu was not found! Could not apply custom editor.");
                    return;
                }
                if (editorActive) OverrideEditor(targetType, typeof(VRCMenuPlus));
                else
                {
                    var menuEditor = ExtendedGetType("VRCExpressionsMenuEditor");
                    if (menuEditor == null)
                    {
                        Debug.LogWarning("[VRCSDK+] VRCExpressionsMenuEditor was not found! Could not apply custom editor.");
                        return;
                    }
                    OverrideEditor(targetType, menuEditor);
                }
                //else OverrideEditor(typeof(VRCExpressionsMenu), Type.GetType("VRCExpressionsMenuEditor, Assembly-CSharp-Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"));
            }

            private static class ControlRenderer
            {
                private const float IconSize = 96;
                private const float IconSpace = IconSize + 3;

                private const float CompactIconSize = 60;
                private const float CompactIconSpace = CompactIconSize + 3;

                public static void DrawControl(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    MainContainer(property);
                    EditorGUILayout.Separator();
                    ParameterContainer(property, parameters);

                    if (property != null)
                    {
                        EditorGUILayout.Separator();

                        switch ((VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("type").intValue)
                        {
                            case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                                RadialContainer(property, parameters);
                                break;
                            case VRCExpressionsMenu.Control.ControlType.SubMenu:
                                SubMenuContainer(property);
                                break;
                            case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                                TwoAxisParametersContainer(property, parameters);
                                EditorGUILayout.Separator();
                                AxisCustomisationContainer(property);
                                break;
                            case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                                FourAxisParametersContainer(property, parameters);
                                EditorGUILayout.Separator();
                                AxisCustomisationContainer(property);
                                break;
                        }
                    }
                }

                public static void DrawControlCompact(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    CompactMainContainer(property, parameters);

                    if (property != null)
                    {
                        switch ((VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("type").intValue)
                        {
                            case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                                RadialContainer(property, parameters);
                                break;
                            case VRCExpressionsMenu.Control.ControlType.SubMenu:
                                SubMenuContainer(property);
                                break;
                            case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                                CompactTwoAxisParametersContainer(property, parameters);
                                //AxisCustomisationContainer(property);
                                break;
                            case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                                CompactFourAxisParametersContainer(property, parameters);
                                //AxisCustomisationContainer(property);
                                break;
                        }
                    }
                }

                #region Main container

                static void MainContainer(SerializedProperty property)
                {
                    var rect = EditorGUILayout
                        .GetControlRect(false, 147);
                    VRCSDKPlusToolbox.Container.GUIBox(ref rect);

                    var nameRect = new Rect(rect.x, rect.y, rect.width - IconSpace, 21);
                    var typeRect = new Rect(rect.x, rect.y + 24, rect.width - IconSpace, 21);
                    var baseStyleRect = new Rect(rect.x, rect.y + 48, rect.width - IconSpace, 21);
                    var iconRect = new Rect(rect.x + rect.width - IconSize, rect.y, IconSize, IconSize);
                    var helpRect = new Rect(rect.x, rect.y + IconSpace, rect.width, 42);

                    DrawName(nameRect, property, true);
                    DrawType(typeRect, property, true);
                    DrawStyle(baseStyleRect, property, true);
                    DrawIcon(iconRect, property);
                    DrawHelp(helpRect, property);
                }

                static void CompactMainContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    var rect = EditorGUILayout.GetControlRect(false, 66);
                    VRCSDKPlusToolbox.Container.GUIBox(ref rect);

                    var halfWidth = (rect.width - CompactIconSpace) / 2;
                    var nameRect = new Rect(rect.x, rect.y, halfWidth - 3, 18);
                    var typeRect = new Rect(rect.x + halfWidth, rect.y, halfWidth - 19, 18);
                    var helpRect = new Rect(typeRect.x + typeRect.width + 1, rect.y, 18, 18);
                    var parameterRect = new Rect(rect.x, rect.y + 21, rect.width - CompactIconSpace, 18);
                    var styleRect = new Rect(rect.x, rect.y + 42, rect.width - CompactIconSize, 18);
                    var iconRect = new Rect(rect.x + rect.width - CompactIconSize, rect.y, CompactIconSize, CompactIconSize);

                    DrawName(nameRect, property, false);
                    DrawType(typeRect, property, false);
                    DrawStyle(styleRect, property, false);

                    if (property != null)
                        GUI.Label(helpRect, new GUIContent(VRCSDKPlusToolbox.GUIContent.Help) { tooltip = GetHelpMessage(property) }, GUIStyle.none);

                    ParameterContainer(property, parameters, parameterRect);

                    DrawIcon(iconRect, property);

                    // ToDo Draw error help if Parameter not found
                }

                static void DrawName(Rect rect, SerializedProperty property, bool drawLabel)
                {
                    if (property == null)
                    {
                        VRCSDKPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    var name = property.FindPropertyRelative("name");

                    if (drawLabel)
                    {
                        var label = new Rect(rect.x, rect.y, 100, rect.height);
                        rect = new Rect(rect.x + 103, rect.y, rect.width - 103, rect.height);

                        GUI.Label(label, "Name");
                    }

                    name.stringValue = EditorGUI.TextField(rect, name.stringValue);
                    if (string.IsNullOrEmpty(name.stringValue)) GUI.Label(rect, "Name", VRCSDKPlusToolbox.Styles.Label.PlaceHolder);
                }

                static void DrawType(Rect rect, SerializedProperty property, bool drawLabel)
                {
                    if (property == null)
                    {
                        VRCSDKPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    if (drawLabel)
                    {
                        var label = new Rect(rect.x, rect.y, 100, rect.height);
                        rect = new Rect(rect.x + 103, rect.y, rect.width - 103, rect.height);

                        GUI.Label(label, "Type");
                    }

                    var controlType = property.FindPropertyRelative("type").ToControlType();
                    var newType = (VRCExpressionsMenu.Control.ControlType)EditorGUI.EnumPopup(rect, controlType);

                    if (newType != controlType)
                        ConversionEntry(property, controlType, newType);
                }

                static void DrawStyle(Rect rect, SerializedProperty property, bool drawLabel)
                {
                    const float toggleSize = 21;

                    if (property == null)
                    {
                        VRCSDKPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    if (drawLabel)
                    {
                        Rect labelRect = new Rect(rect.x, rect.y, 100, rect.height);
                        rect = new Rect(rect.x + 103, rect.y, rect.width - 103, rect.height);
                        GUI.Label(labelRect, "Style");
                    }

                    Rect colorRect = new Rect(rect.x, rect.y, rect.width - (toggleSize + 3) * 2, rect.height);
                    Rect boldRect = new Rect(colorRect.x + colorRect.width, rect.y, toggleSize, rect.height);
                    Rect italicRect = new Rect(boldRect); italicRect.x += italicRect.width + 3; boldRect.width = toggleSize;
                    string rawName = property.FindPropertyRelative("name").stringValue;
                    Color textColor = Color.white;

                    var isBold = rawName.Contains("<b>") && rawName.Contains("</b>");
                    var isItalic = rawName.Contains("<i>") && rawName.Contains("</i>");
                    var m = Regex.Match(rawName, @"<color=(#[0-9|A-F]{6,8})>");
                    if (m.Success)
                    {
                        if (rawName.Contains("</color>"))
                        {
                            if (ColorUtility.TryParseHtmlString(m.Groups[1].Value, out Color newColor))
                                textColor = newColor;

                        }
                    }


                    EditorGUI.BeginChangeCheck();
                    textColor = EditorGUI.ColorField(colorRect, textColor);
                    if (EditorGUI.EndChangeCheck())
                    {
                        rawName = Regex.Replace(rawName, @"</?color=?.*?>", string.Empty);
                        rawName = $"<color=#{ColorUtility.ToHtmlStringRGB(textColor)}>{rawName}</color>";
                    }
                    
                    void SetCharTag(char c, bool state)
                    {
                        rawName = !state ?
                            Regex.Replace(rawName, $@"</?{c}>", string.Empty) : 
                            $"<{c}>{rawName}</{c}>";
                    }

                    w_MakeRectLinkCursor(boldRect);
                    EditorGUI.BeginChangeCheck();
                    isBold = GUI.Toggle(boldRect, isBold, new GUIContent("<b>b</b>","Bold"), VRCSDKPlusToolbox.Styles.letterButton);
                    if (EditorGUI.EndChangeCheck()) SetCharTag('b', isBold);

                    w_MakeRectLinkCursor(italicRect);
                    EditorGUI.BeginChangeCheck();
                    isItalic = GUI.Toggle(italicRect, isItalic, new GUIContent("<i>i</i>", "Italic"), VRCSDKPlusToolbox.Styles.letterButton);
                    if (EditorGUI.EndChangeCheck()) SetCharTag('i', isItalic);


                    property.FindPropertyRelative("name").stringValue = rawName;
                }
                static void DrawIcon(Rect rect, SerializedProperty property)
                {
                    if (property == null)
                        VRCSDKPlusToolbox.Placeholder.GUI(rect);
                    else
                    {
                        var value = property.FindPropertyRelative("icon");

                        value.objectReferenceValue = EditorGUI.ObjectField(
                            rect,
                            string.Empty,
                            value.objectReferenceValue,
                            typeof(Texture2D),
                            false
                        );
                    }
                }
                static void DrawHelp(Rect rect, SerializedProperty property)
                {
                    if (property == null)
                    {
                        VRCSDKPlusToolbox.Placeholder.GUI(rect);
                        return;
                    }

                    string message = GetHelpMessage(property);
                    EditorGUI.HelpBox(rect, message, MessageType.Info);
                }
                static string GetHelpMessage(SerializedProperty property)
                {
                    switch (property.FindPropertyRelative("type").ToControlType())
                    {
                        case VRCExpressionsMenu.Control.ControlType.Button:
                            return "Click or hold to activate. The button remains active for a minimum 0.2s.\nWhile active the (Parameter) is set to (Value).\nWhen inactive the (Parameter) is reset to zero.";
                        case VRCExpressionsMenu.Control.ControlType.Toggle:
                            return "Click to toggle on or off.\nWhen turned on the (Parameter) is set to (Value).\nWhen turned off the (Parameter) is reset to zero.";
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            return "Opens another expression menu.\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.";
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            return "Puppet menu that maps the joystick to two parameters (-1 to +1).\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.";
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            return "Puppet menu that maps the joystick to four parameters (0 to 1).\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.";
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                            return "Puppet menu that sets a value based on joystick rotation. (0 to 1)\nWhen opened the (Parameter) is set to (Value).\nWhen closed (Parameter) is reset to zero.";
                        default:
                            return "ERROR: Unable to load message - Invalid control type";
                    }
                }

                #endregion

                #region Type Conversion

                private static void ConversionEntry(SerializedProperty property, VRCExpressionsMenu.Control.ControlType tOld, VRCExpressionsMenu.Control.ControlType tNew)
                {
                    // Is old one button / toggle, and new one not?
                    if (
                            (tOld == VRCExpressionsMenu.Control.ControlType.Button || tOld == VRCExpressionsMenu.Control.ControlType.Toggle) &&
                            (tNew != VRCExpressionsMenu.Control.ControlType.Button && tNew != VRCExpressionsMenu.Control.ControlType.Toggle)
                        )
                        // Reset parameter
                        property.FindPropertyRelative("parameter").FindPropertyRelative("name").stringValue = "";
                    else if (
                        (tOld != VRCExpressionsMenu.Control.ControlType.Button && tOld != VRCExpressionsMenu.Control.ControlType.Toggle) &&
                        (tNew == VRCExpressionsMenu.Control.ControlType.Button || tNew == VRCExpressionsMenu.Control.ControlType.Toggle)
                    )
                        SetupSubParameters(property, tNew);

                    // Is either a submenu
                    if (tOld == VRCExpressionsMenu.Control.ControlType.SubMenu || tNew == VRCExpressionsMenu.Control.ControlType.SubMenu)
                        SetupSubParameters(property, tNew);

                    // Is either Puppet)
                    if (IsPuppetConversion(tOld, tNew))
                        DoPuppetConversion(property, tNew);
                    else if (
                        tNew == VRCExpressionsMenu.Control.ControlType.RadialPuppet ||
                        tNew == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ||
                        tNew == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet
                    )
                        SetupSubParameters(property, tNew);

                    property.FindPropertyRelative("type").enumValueIndex = tNew.GetEnumValueIndex();
                }

                private static bool IsPuppetConversion(VRCExpressionsMenu.Control.ControlType tOld, VRCExpressionsMenu.Control.ControlType tNew)
                {
                    return (
                               tOld == VRCExpressionsMenu.Control.ControlType.RadialPuppet ||
                               tOld == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ||
                               tOld == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet
                           ) &&
                           (
                               tNew == VRCExpressionsMenu.Control.ControlType.RadialPuppet ||
                               tNew == VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet ||
                               tNew == VRCExpressionsMenu.Control.ControlType.FourAxisPuppet
                           );
                }

                private static void DoPuppetConversion(SerializedProperty property, VRCExpressionsMenu.Control.ControlType tNew)
                {
                    var subParameters = property.FindPropertyRelative("subParameters");
                    var sub0 = subParameters.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue;
                    var sub1 = subParameters.arraySize > 1
                        ? subParameters.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue
                        : string.Empty;

                    subParameters.ClearArray();
                    subParameters.InsertArrayElementAtIndex(0);
                    subParameters.GetArrayElementAtIndex(0).FindPropertyRelative("name").stringValue = sub0;

                    // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                    switch (tNew)
                    {
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(1);
                            subParameters.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue = sub1;
                            break;

                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(1);
                            subParameters.GetArrayElementAtIndex(1).FindPropertyRelative("name").stringValue = sub1;
                            subParameters.InsertArrayElementAtIndex(2);
                            subParameters.GetArrayElementAtIndex(2).FindPropertyRelative("name").stringValue = "";
                            subParameters.InsertArrayElementAtIndex(3);
                            subParameters.GetArrayElementAtIndex(3).FindPropertyRelative("name").stringValue = "";
                            break;
                    }
                }

                private static void SetupSubParameters(SerializedProperty property, VRCExpressionsMenu.Control.ControlType type)
                {
                    var subParameters = property.FindPropertyRelative("subParameters");
                    subParameters.ClearArray();

                    switch (type)
                    {
                        case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                        case VRCExpressionsMenu.Control.ControlType.SubMenu:
                            subParameters.InsertArrayElementAtIndex(0);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(0);
                            subParameters.InsertArrayElementAtIndex(1);
                            break;
                        case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                            subParameters.InsertArrayElementAtIndex(0);
                            subParameters.InsertArrayElementAtIndex(1);
                            subParameters.InsertArrayElementAtIndex(2);
                            subParameters.InsertArrayElementAtIndex(3);
                            break;
                    }
                }

                #endregion

                /*static void DrawParameterNotFound(string parameter)
                {
                    EditorGUILayout.HelpBox(
                        $"Parameter not found on the active avatar descriptor ({parameter})",
                        MessageType.Warning
                    );
                }*/



                #region BuildParameterArray

                static void BuildParameterArray(
                    string name,
                    VRCExpressionParameters parameters,
                    out int index,
                    out string[] parametersAsString
                )
                {
                    index = -2;
                    if (!parameters)
                    {
                        parametersAsString = Array.Empty<string>();
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        for (var i = 0; i < parameters.parameters.Length; i++)
                        {
                            if (parameters.parameters[i].name != name) continue;

                            index = i + 1;
                            break;
                        }
                    }
                    else
                        index = -1;

                    parametersAsString = new string[parameters.parameters.Length + 1];
                    parametersAsString[0] = "[None]";
                    for (var i = 0; i < parameters.parameters.Length; i++)
                    {
                        switch (parameters.parameters[i].valueType)
                        {
                            case VRCExpressionParameters.ValueType.Int:
                                parametersAsString[i + 1] = $"{parameters.parameters[i].name} [int]";
                                break;
                            case VRCExpressionParameters.ValueType.Float:
                                parametersAsString[i + 1] = $"{parameters.parameters[i].name} [float]";
                                break;
                            case VRCExpressionParameters.ValueType.Bool:
                                parametersAsString[i + 1] = $"{parameters.parameters[i].name} [bool]";
                                break;
                        }
                    }
                }

                static void BuildParameterArray(
                    string name,
                    VRCExpressionParameters parameters,
                    out int index,
                    out VRCExpressionParameters.Parameter[] filteredParameters,
                    out string[] filteredParametersAsString,
                    VRCExpressionParameters.ValueType filter
                )
                {
                    index = -2;
                    if (!parameters)
                    {
                        filteredParameters = Array.Empty<VRCExpressionParameters.Parameter>();
                        filteredParametersAsString = Array.Empty<string>();
                        return;
                    }

                    filteredParameters = parameters.parameters.Where(p => p.valueType == filter).ToArray();

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        for (var i = 0; i < filteredParameters.Length; i++)
                        {
                            if (filteredParameters[i].name != name) continue;

                            index = i + 1;
                            break;
                        }
                    }
                    else
                        index = -1;

                    filteredParametersAsString = new string[filteredParameters.Length + 1];
                    filteredParametersAsString[0] = "[None]";
                    for (var i = 0; i < filteredParameters.Length; i++)
                    {
                        switch (filteredParameters[i].valueType)
                        {
                            case VRCExpressionParameters.ValueType.Int:
                                filteredParametersAsString[i + 1] = $"{filteredParameters[i].name} [int]";
                                break;
                            case VRCExpressionParameters.ValueType.Float:
                                filteredParametersAsString[i + 1] = $"{filteredParameters[i].name} [float]";
                                break;
                            case VRCExpressionParameters.ValueType.Bool:
                                filteredParametersAsString[i + 1] = $"{filteredParameters[i].name} [bool]";
                                break;
                        }
                    }
                }

                #endregion

                #region DrawParameterSelector

                struct ParameterSelectorOptions
                {
                    public Action extraGUI;
                    public Rect rect;
                    public bool required;

                    public ParameterSelectorOptions(Rect rect, bool required, Action extraGUI = null)
                    {
                        this.required = required;
                        this.rect = rect;
                        this.extraGUI = extraGUI;
                    }

                    public ParameterSelectorOptions(Rect rect, Action extraGUI = null)
                    {
                        this.required = false;
                        this.rect = rect;
                        this.extraGUI = extraGUI;
                    }

                    public ParameterSelectorOptions(bool required, Action extraGUI = null)
                    {
                        this.required = required;
                        this.rect = default;
                        this.extraGUI = extraGUI;
                    }
                }

                private static bool DrawParameterSelector(
                    string label,
                    SerializedProperty property,
                    VRCExpressionParameters parameters,
                    ParameterSelectorOptions options = default
                )
                {
                    BuildParameterArray(
                        property.FindPropertyRelative("name").stringValue,
                        parameters,
                        out var index,
                        out var parametersAsString
                    );
                    return DrawParameterSelection__BASE(
                        label,
                        property,
                        index,
                        parameters,
                        parameters?.parameters,
                        parametersAsString,
                        false,
                        options
                    );
                }

                private static bool DrawParameterSelector(
                    string label,
                    SerializedProperty property,
                    VRCExpressionParameters parameters,
                    VRCExpressionParameters.ValueType filter,
                    ParameterSelectorOptions options = default
                )
                {
                    BuildParameterArray(
                        property.FindPropertyRelative("name").stringValue,
                        parameters,
                        out var index,
                        out var filteredParameters,
                        out var parametersAsString,
                        filter
                    );
                    return DrawParameterSelection__BASE(
                        label,
                        property,
                        index,
                        parameters,
                        filteredParameters,
                        parametersAsString,
                        true,
                        options
                    );
                }

                private static bool DrawParameterSelection__BASE(
                    string label,
                    SerializedProperty property,
                    int index,
                    VRCExpressionParameters targetParameters,
                    VRCExpressionParameters.Parameter[] parameters,
                    string[] parametersAsString,
                    bool isFiltered,
                    ParameterSelectorOptions options
                )
                {
                    var isEmpty = index == -1;
                    var isMissing = index == -2;
                    bool willWarn = isMissing || options.required && isEmpty;
                    string parameterName = property.FindPropertyRelative("name").stringValue;
                    string warnMsg = targetParameters ? isMissing ? isFiltered ?
                                $"Parameter ({parameterName}) not found or invalid" :
                                $"Parameter ({parameterName}) not found on the active avatar descriptor" :
                            "Parameter is blank. Control may be dysfunctional." :
                        VRCSDKPlusToolbox.GUIContent.MissingParametersTooltip;

                    var rectNotProvided = options.rect == default;
                    using (new GUILayout.HorizontalScope())
                    {
                        const float CONTENT_ADD_WIDTH = 50;
                        const float CONTENT_WARN_WIDTH = 18;
                        const float CONTENT_DROPDOWN_WIDTH = 20;
                        //const float CONTENT_TEXT_FIELD_PORTION = 0.25f;
                        float missingFullWidth = CONTENT_ADD_WIDTH + CONTENT_WARN_WIDTH + 2;

                        bool hasLabel = !string.IsNullOrEmpty(label);

                        if (rectNotProvided) options.rect = EditorGUILayout.GetControlRect(false, 18);

                        var name = property.FindPropertyRelative("name");

                        Rect labelRect = new Rect(options.rect) { width = hasLabel ? 120 : 0 };
                        Rect textfieldRect = new Rect(labelRect) { x = labelRect.x + labelRect.width, width = options.rect.width - labelRect.width - CONTENT_DROPDOWN_WIDTH - 2 };
                        Rect dropdownRect = new Rect(textfieldRect) { x = textfieldRect.x + textfieldRect.width, width = CONTENT_DROPDOWN_WIDTH };
                        Rect addRect = Rect.zero;
                        Rect warnRect = Rect.zero;

                        if (targetParameters && isMissing)
                        {
                            textfieldRect.width -= missingFullWidth;
                            dropdownRect.x -= missingFullWidth;
                            addRect = new Rect(options.rect) { x = textfieldRect.x + textfieldRect.width + CONTENT_DROPDOWN_WIDTH + 2, width = CONTENT_ADD_WIDTH };
                            warnRect = new Rect(addRect) { x = addRect.x + addRect.width, width = CONTENT_WARN_WIDTH };
                        }
                        else if (!targetParameters || options.required && isEmpty)
                        {
                            textfieldRect.width -= CONTENT_WARN_WIDTH;
                            dropdownRect.x -= CONTENT_WARN_WIDTH;
                            warnRect = new Rect(dropdownRect) { x = dropdownRect.x + dropdownRect.width, width = CONTENT_WARN_WIDTH };
                        }

                        if (hasLabel) GUI.Label(labelRect, label);
                        using (new EditorGUI.DisabledScope(!targetParameters || parametersAsString.Length <= 1))
                        {
                            var newIndex = EditorGUI.Popup(dropdownRect, string.Empty, index, parametersAsString);
                            if (index != newIndex)
                                name.stringValue = newIndex == 0 ? string.Empty : parameters[newIndex - 1].name;
                        }

                        name.stringValue = EditorGUI.TextField(textfieldRect, name.stringValue);
                        if (string.IsNullOrEmpty(name.stringValue)) GUI.Label(textfieldRect, "Parameter", VRCSDKPlusToolbox.Styles.Label.PlaceHolder);
                        if (willWarn) GUI.Label(warnRect, new GUIContent(VRCSDKPlusToolbox.GUIContent.Warn) { tooltip = warnMsg });

                        if (isMissing)
                        {
                            int dummy;

                            if (!isFiltered)
                            {
                                dummy = EditorGUI.Popup(addRect, -1, Enum.GetNames(typeof(VRCExpressionParameters.ValueType)));

                                addRect.x += 3;
                                GUI.Label(addRect, "Add");
                            }
                            else dummy = GUI.Button(addRect, "Add") ? 1 : -1;

                            if (dummy != -1)
                            {
                                SerializedObject so = new SerializedObject(targetParameters);
                                var param = so.FindProperty("parameters");
                                var prop = param.GetArrayElementAtIndex(param.arraySize++);
                                prop.FindPropertyRelative("valueType").enumValueIndex = dummy;
                                prop.FindPropertyRelative("name").stringValue = name.stringValue;
                                prop.FindPropertyRelative("saved").boolValue = true;
                                try{ prop.FindPropertyRelative("networkSynced").boolValue = true; } catch{}
                                so.ApplyModifiedProperties();
                            }
                        }

                        options.extraGUI?.Invoke();
                    }

                    return isMissing;
                }

                #endregion

                #region Parameter conainer

                static void ParameterContainer(
                    SerializedProperty property,
                    VRCExpressionParameters parameters,
                    Rect rect = default
                )
                {
                    var rectProvided = rect != default;

                    if (property?.FindPropertyRelative("parameter") == null)
                    {
                        if (rectProvided)
                            VRCSDKPlusToolbox.Placeholder.GUI(rect);
                        else
                        {
                            VRCSDKPlusToolbox.Container.BeginLayout();
                            VRCSDKPlusToolbox.Placeholder.GUILayout(18);
                            VRCSDKPlusToolbox.Container.EndLayout();
                        }
                    }
                    else
                    {
                        if (!rectProvided) VRCSDKPlusToolbox.Container.BeginLayout();

                        float CONTENT_VALUE_SELECTOR_WIDTH = 50;
                        Rect selectorRect = default;
                        Rect valueRect = default;

                        if (rectProvided)
                        {
                            selectorRect = new Rect(rect.x, rect.y, rect.width - CONTENT_VALUE_SELECTOR_WIDTH - 3,
                                rect.height);
                            valueRect = new Rect(selectorRect.x + selectorRect.width + 3, rect.y,
                                CONTENT_VALUE_SELECTOR_WIDTH, rect.height);
                        }

                        var parameter = property.FindPropertyRelative("parameter");

                        var t = (VRCExpressionsMenu.Control.ControlType)property.FindPropertyRelative("type").intValue;
                        bool isRequired = t == VRCExpressionsMenu.Control.ControlType.Button || t == VRCExpressionsMenu.Control.ControlType.Toggle;
                        DrawParameterSelector(rectProvided ? string.Empty : "Parameter", parameter, parameters, new ParameterSelectorOptions()
                        {
                            rect = selectorRect,
                            required = isRequired,
                            extraGUI = () =>
                            {
                                #region Value selector

                                var parameterName = parameter.FindPropertyRelative("name");
                                var param = parameters?.parameters.FirstOrDefault(p => p.name == parameterName.stringValue);

                                // Check what type the parameter is

                                var value = property.FindPropertyRelative("value");
                                switch (param?.valueType)
                                {
                                    case VRCExpressionParameters.ValueType.Int:
                                        value.floatValue = Mathf.Clamp(rectProvided ?
                                            EditorGUI.IntField(valueRect, (int)value.floatValue) :
                                            EditorGUILayout.IntField((int)value.floatValue, GUILayout.Width(CONTENT_VALUE_SELECTOR_WIDTH)), 0f, 255f);
                                        break;

                                    case VRCExpressionParameters.ValueType.Float:
                                        value.floatValue = Mathf.Clamp(rectProvided ?
                                            EditorGUI.FloatField(valueRect, value.floatValue) :
                                            EditorGUILayout.FloatField(value.floatValue, GUILayout.Width(CONTENT_VALUE_SELECTOR_WIDTH)), -1, 1);
                                        break;

                                    case VRCExpressionParameters.ValueType.Bool:
                                        using (new EditorGUI.DisabledScope(true))
                                        {
                                            if (rectProvided) EditorGUI.TextField(valueRect, string.Empty);
                                            else EditorGUILayout.TextField(string.Empty, GUILayout.Width(CONTENT_VALUE_SELECTOR_WIDTH));
                                        }

                                        value.floatValue = 1f;
                                        break;

                                    default:
                                        value.floatValue = Mathf.Clamp(rectProvided ?
                                            EditorGUI.FloatField(valueRect, value.floatValue) :
                                            EditorGUILayout.FloatField(value.floatValue, GUILayout.Width(CONTENT_VALUE_SELECTOR_WIDTH)), -1, 255);
                                        break;
                                }
                                #endregion
                            }
                        });

                        if (!rectProvided)
                            VRCSDKPlusToolbox.Container.EndLayout();
                    }
                }

                #endregion

                #region Miscellaneous containers

                static void RadialContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    using (new VRCSDKPlusToolbox.Container.Vertical())
                        DrawParameterSelector(
                            "Rotation",
                            property.FindPropertyRelative("subParameters").GetArrayElementAtIndex(0),
                            parameters,
                            VRCExpressionParameters.ValueType.Float,
                            new ParameterSelectorOptions(true)
                        );
                }

                static void SubMenuContainer(SerializedProperty property)
                {
                    using (new VRCSDKPlusToolbox.Container.Vertical())
                    {
                        var subMenu = property.FindPropertyRelative("subMenu");
                        var nameProperty = property.FindPropertyRelative("name");
                        bool emptySubmenu = subMenu.objectReferenceValue == null;

                        using (new GUILayout.HorizontalScope())
                        {
                            EditorGUILayout.PropertyField(subMenu);
                            if (emptySubmenu)
                            {
                                using (new EditorGUI.DisabledScope(_currentNode?.Value == null))
                                    if (GUILayout.Button("New", GUILayout.Width(40)))
                                    {
                                        var m = _currentNode.Value;
                                        var path = AssetDatabase.GetAssetPath(m);
                                        if (string.IsNullOrEmpty(path))
                                            path = $"Assets/{m.name}.asset";
                                        var parentPath = Path.GetDirectoryName(path);
                                        var assetName = string.IsNullOrEmpty(nameProperty?.stringValue) ? $"{m.name} SubMenu.asset" : $"{nameProperty.stringValue} Menu.asset";
                                        var newMenuPath = VRCSDKPlusToolbox.ReadyAssetPath(parentPath, assetName, true);

                                        var newMenu = CreateInstance<VRCExpressionsMenu>();
                                        if (newMenu.controls == null)
                                            newMenu.controls = new List<VRCExpressionsMenu.Control>();

                                        AssetDatabase.CreateAsset(newMenu, newMenuPath);
                                        subMenu.objectReferenceValue = newMenu;
                                    }
                                GUILayout.Label(new GUIContent(VRCSDKPlusToolbox.GUIContent.Warn) { tooltip = "Submenu is empty. This control has no use." }, VRCSDKPlusToolbox.Styles.icon);
                            }
                            using (new EditorGUI.DisabledScope(emptySubmenu))
                            {
                                if (ClickableButton(VRCSDKPlusToolbox.GUIContent.Folder, VRCSDKPlusToolbox.Styles.icon))
                                    Selection.activeObject = subMenu.objectReferenceValue;
                                if (ClickableButton(VRCSDKPlusToolbox.GUIContent.Clear, VRCSDKPlusToolbox.Styles.icon))
                                    subMenu.objectReferenceValue = null;
                            }
                        }
                    }
                }

                static void CompactTwoAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    using (new VRCSDKPlusToolbox.Container.Vertical())
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            using (new GUILayout.HorizontalScope())
                                GUILayout.Label("Axis Parameters", VRCSDKPlusToolbox.Styles.Label.Centered);


                            using (new GUILayout.HorizontalScope())
                            {
                                GUILayout.Label("Name -", VRCSDKPlusToolbox.Styles.Label.Centered);
                                GUILayout.Label("Name +", VRCSDKPlusToolbox.Styles.Label.Centered);
                            }
                        }

                        var subs = property.FindPropertyRelative("subParameters");
                        var sub0 = subs.GetArrayElementAtIndex(0);
                        var sub1 = subs.GetArrayElementAtIndex(1);

                        var labels = SafeGetLabels(property);

                        using (new GUILayout.HorizontalScope())
                        {
                            var rect = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Horizontal",
                                    sub0,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(rect, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                            {
                                DrawLabel(labels.GetArrayElementAtIndex(0), "Left");
                                DrawLabel(labels.GetArrayElementAtIndex(1), "Right");
                            }
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var rect = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Vertical",
                                    sub1,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(rect, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                            {
                                DrawLabel(labels.GetArrayElementAtIndex(2), "Down");
                                DrawLabel(labels.GetArrayElementAtIndex(3), "Up");
                            }
                        }
                    }

                }
                static void CompactFourAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    using (new VRCSDKPlusToolbox.Container.Vertical())
                    {
                        using (new GUILayout.HorizontalScope())
                        {
                            var headerRect = EditorGUILayout.GetControlRect();
                            var r1 = new Rect(headerRect) { width = headerRect.width / 2 };
                            var r2 = new Rect(r1) { x = r1.x + r1.width };
                            GUI.Label(r1, "Axis Parameters", VRCSDKPlusToolbox.Styles.Label.Centered);
                            GUI.Label(r2, "Name", VRCSDKPlusToolbox.Styles.Label.Centered);
                        }

                        var subs = property.FindPropertyRelative("subParameters");
                        var sub0 = subs.GetArrayElementAtIndex(0);
                        var sub1 = subs.GetArrayElementAtIndex(1);
                        var sub2 = subs.GetArrayElementAtIndex(2);
                        var sub3 = subs.GetArrayElementAtIndex(3);

                        var labels = SafeGetLabels(property);

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Up",
                                    sub0,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(0), "Name");
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Right",
                                    sub1,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(1), "Name");
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Down",
                                    sub2,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(2), "Name");
                        }

                        using (new GUILayout.HorizontalScope())
                        {
                            var r = EditorGUILayout.GetControlRect();
                            using (new GUILayout.HorizontalScope())
                            {
                                DrawParameterSelector(
                                    "Left",
                                    sub3,
                                    parameters,
                                    VRCExpressionParameters.ValueType.Float,
                                    new ParameterSelectorOptions(r, true)
                                );
                            }

                            using (new GUILayout.HorizontalScope())
                                DrawLabel(labels.GetArrayElementAtIndex(3), "Name");
                        }
                    }

                }
                static void TwoAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    VRCSDKPlusToolbox.Container.BeginLayout();

                    GUILayout.Label("Axis Parameters", VRCSDKPlusToolbox.Styles.Label.Centered);

                    var subs = property.FindPropertyRelative("subParameters");
                    var sub0 = subs.GetArrayElementAtIndex(0);
                    var sub1 = subs.GetArrayElementAtIndex(1);

                    DrawParameterSelector(
                        "Horizontal",
                        sub0,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Vertical",
                        sub1,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    VRCSDKPlusToolbox.Container.EndLayout();
                }

                static void FourAxisParametersContainer(SerializedProperty property, VRCExpressionParameters parameters)
                {
                    VRCSDKPlusToolbox.Container.BeginLayout("Axis Parameters");

                    var subs = property.FindPropertyRelative("subParameters");
                    var sub0 = subs.GetArrayElementAtIndex(0);
                    var sub1 = subs.GetArrayElementAtIndex(1);
                    var sub2 = subs.GetArrayElementAtIndex(2);
                    var sub3 = subs.GetArrayElementAtIndex(3);

                    DrawParameterSelector(
                        "Up",
                        sub0,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Right",
                        sub1,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Down",
                        sub2,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    DrawParameterSelector(
                        "Left",
                        sub3,
                        parameters,
                        VRCExpressionParameters.ValueType.Float,
                        new ParameterSelectorOptions(true)
                    );

                    VRCSDKPlusToolbox.Container.EndLayout();
                }

                static void AxisCustomisationContainer(SerializedProperty property)
                {
                    var labels = SafeGetLabels(property);

                    using (new VRCSDKPlusToolbox.Container.Vertical("Customization"))
                    {
                        DrawLabel(labels.GetArrayElementAtIndex(0), "Up");
                        DrawLabel(labels.GetArrayElementAtIndex(1), "Right");
                        DrawLabel(labels.GetArrayElementAtIndex(2), "Down");
                        DrawLabel(labels.GetArrayElementAtIndex(3), "Left");
                    }
                }

                static SerializedProperty SafeGetLabels(SerializedProperty property)
                {
                    var labels = property.FindPropertyRelative("labels");

                    labels.arraySize = 4;
                    var l0 = labels.GetArrayElementAtIndex(0);
                    if (l0 == null)
                    {
                        var menu = (VRCExpressionsMenu)labels.serializedObject.targetObject;
                        var index = menu.controls.FindIndex(property.objectReferenceValue);
                        menu.controls[index].labels = new[]
                        {
                            new VRCExpressionsMenu.Control.Label(),
                            new VRCExpressionsMenu.Control.Label(),
                            new VRCExpressionsMenu.Control.Label(),
                            new VRCExpressionsMenu.Control.Label()
                        };
                    }

                    if (labels.GetArrayElementAtIndex(0) == null)
                        Debug.Log("ITEM IS NULL");

                    return labels;
                }

                static void DrawLabel(SerializedProperty property, string type)
                {
                    bool compact = VRCSDKPlusToolbox.Preferences.CompactMode;
                    float imgWidth = compact ? 28 : 58;
                    float imgHeight = compact ? EditorGUIUtility.singleLineHeight : 58;

                    var imgProperty = property.FindPropertyRelative("icon");
                    var nameProperty = property.FindPropertyRelative("name");
                    if (!compact) EditorGUILayout.BeginVertical("helpbox");

                    using (new GUILayout.HorizontalScope())
                    {
                        using (new GUILayout.VerticalScope())
                        {
                            if (!compact)
                                using (new EditorGUI.DisabledScope(true))
                                    EditorGUILayout.LabelField("Axis", type, VRCSDKPlusToolbox.Styles.Label.LabelDropdown);

                            EditorGUILayout.PropertyField(nameProperty, compact ? GUIContent.none : new GUIContent("Name"));
                            var nameRect = GUILayoutUtility.GetLastRect();
                            if (compact && string.IsNullOrEmpty(nameProperty.stringValue)) GUI.Label(nameRect, $"{type}", VRCSDKPlusToolbox.Styles.Label.PlaceHolder);
                        }

                        imgProperty.objectReferenceValue = EditorGUILayout.ObjectField(imgProperty.objectReferenceValue, typeof(Texture2D), false, GUILayout.Width(imgWidth), GUILayout.Height(imgHeight));
                    }

                    if (!compact) EditorGUILayout.EndHorizontal();

                }

                #endregion
            }

        }


        #region Helper Methods
        #region Clickables
        internal static bool ClickableButton(string     label, GUIStyle                 style = null, params GUILayoutOption[] options) => ClickableButton(new GUIContent(label), style, options);
        internal static bool ClickableButton(string     label, params GUILayoutOption[] options) => ClickableButton(new GUIContent(label), null, options);
        internal static bool ClickableButton(GUIContent label, params GUILayoutOption[] options) => ClickableButton(label,                 null, options);
        internal static bool ClickableButton(GUIContent label, GUIStyle style = null, params GUILayoutOption[] options)
        {
            if (style == null)
                style = GUI.skin.button;
            bool clicked = GUILayout.Button(label, style, options);
            if (GUI.enabled) w_MakeRectLinkCursor();
            return clicked;
        }
        internal static void w_MakeRectLinkCursor(Rect rect = default)
        {
            if (!GUI.enabled) return;
            if (Event.current.type == EventType.Repaint)
            {
                if (rect == default) rect = GUILayoutUtility.GetLastRect();
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }
        }
        internal static bool w_MakeRectClickable(Rect rect = default)
        {
            if (rect == default) rect = GUILayoutUtility.GetLastRect();
            w_MakeRectLinkCursor(rect);
            var e = Event.current;
            return e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition);
        }
        #endregion

        private static void Link(string label, string url)
        {
            var bgcolor = GUI.backgroundColor;
            GUI.backgroundColor = Color.clear;

            if (GUILayout.Button(new GUIContent(label, url), VRCSDKPlusToolbox.Styles.Label.faintLinkLabel))
                Application.OpenURL(url);
            w_UnderlineLastRectOnHover();
            
            GUI.backgroundColor = bgcolor;
        }
        
        internal static void w_UnderlineLastRectOnHover(Color? color = null)
        {
            if (color == null) color = new Color(0.3f, 0.7f, 1);
            if (Event.current.type == EventType.Repaint)
            {
                var rect = GUILayoutUtility.GetLastRect();
                var mp = Event.current.mousePosition;
                if (rect.Contains(mp)) EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 1, rect.width, 1), color.Value);
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            }

        }
        
        internal static System.Type ExtendedGetType(string typeName)
        {
            var myType = System.Type.GetType(typeName);
            if (myType != null)
                return myType;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var types = assembly.GetTypes();
                myType = types.FirstOrDefault(t  => t.FullName == typeName);
                if (myType != null)
                    return myType;
                myType = types.FirstOrDefault(t => t.Name == typeName);
                if (myType != null)
                    return myType;
            }
            return null;
        }
        internal static void RefreshAvatar(ref VRCAvatarDescriptor avatar, ref VRCAvatarDescriptor[] validAvatars, System.Action OnAvatarChanged = null, System.Func<VRCAvatarDescriptor, bool> favoredAvatar = null)
        {
            validAvatars = Object.FindObjectsOfType<VRCAvatarDescriptor>();
            if (avatar) return;

            if (validAvatars.Length > 0)
            {
                if (favoredAvatar != null)
                    avatar = validAvatars.FirstOrDefault(favoredAvatar) ?? validAvatars[0];
                else avatar = validAvatars[0];
            }

            OnAvatarChanged?.Invoke();
        }

        internal static bool DrawAdvancedAvatarFull(ref VRCAvatarDescriptor avatar, VRCAvatarDescriptor[] validAvatars, System.Action OnAvatarChanged = null, bool warnNonHumanoid = true, bool warnPrefab = true, bool warnDoubleFX = true, string label = "Avatar", string tooltip = "The Targeted VRCAvatar", System.Action ExtraGUI = null)
            => DrawAdvancedAvatarField(ref avatar, validAvatars, OnAvatarChanged, label, tooltip, ExtraGUI) && DrawAdvancedAvatarWarning(avatar, warnNonHumanoid, warnPrefab, warnDoubleFX);

        private static VRCAvatarDescriptor DrawAdvancedAvatarField(ref VRCAvatarDescriptor avatar, VRCAvatarDescriptor[] validAvatars, System.Action OnAvatarChanged = null, string label = "Avatar", string tooltip = "The Targeted VRCAvatar", System.Action ExtraGUI = null)
        {
            using (new GUILayout.HorizontalScope())
            {
                var avatarContent = new GUIContent(label, tooltip);
                if (validAvatars == null || validAvatars.Length <= 0) EditorGUILayout.LabelField(avatarContent, new GUIContent("No Avatar Descriptors Found"));
                else
                {
                    using (var change = new EditorGUI.ChangeCheckScope())
                    {
                        int dummy = EditorGUILayout.Popup(avatarContent, avatar ? Array.IndexOf(validAvatars, avatar) : -1, validAvatars.Where(a => a).Select(x => x.name).ToArray());
                        if (change.changed)
                        {
                            avatar = validAvatars[dummy];
                            EditorGUIUtility.PingObject(avatar);
                            OnAvatarChanged?.Invoke();
                        }
                    }
                }

                ExtraGUI?.Invoke();
            }
            return avatar;
        }

        private static bool DrawAdvancedAvatarWarning(VRCAvatarDescriptor avatar, bool warnNonHumanoid = true, bool warnPrefab = true, bool warnDoubleFX = true)
        {
            return (!warnPrefab || !DrawPrefabWarning(avatar)) && (!warnDoubleFX || !DrawDoubleFXWarning(avatar, warnNonHumanoid));
        }

        private static bool DrawPrefabWarning(VRCAvatarDescriptor avatar)
        {
            if (!avatar) return false;
            bool isPrefab = PrefabUtility.IsPartOfAnyPrefab(avatar.gameObject);
            if (isPrefab)
            {
                EditorGUILayout.HelpBox("Target Avatar is a part of a prefab. Prefab unpacking is required.", MessageType.Error);
                if (GUILayout.Button("Unpack")) PrefabUtility.UnpackPrefabInstance(avatar.gameObject, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }
            return isPrefab;
        }
        private static bool DrawDoubleFXWarning(VRCAvatarDescriptor avatar, bool warnNonHumanoid = true)
        {
            if (!avatar) return false;
            var layers = avatar.baseAnimationLayers;

            if (layers.Length > 3)
            {
                var isDoubled = layers[3].type == layers[4].type;
                if (isDoubled)
                {
                    EditorGUILayout.HelpBox("Your Avatar's Action playable layer is set as FX. This is an uncommon bug.", MessageType.Error);
                    if (GUILayout.Button("Fix"))
                    {
                        avatar.baseAnimationLayers[3].type = VRCAvatarDescriptor.AnimLayerType.Action;
                        EditorUtility.SetDirty(avatar);
                    }
                }

                return isDoubled;
            }

            if (warnNonHumanoid)
                EditorGUILayout.HelpBox("Your Avatar's descriptor is set as Non-Humanoid! Please make sure that your Avatar's rig is Humanoid.", MessageType.Error);
            return warnNonHumanoid;

        }

        private static void GreenLog(string msg) => Debug.Log($"<color=green>[VRCSDK+] </color>{msg}");
        #endregion

        #region Automated Methods
        private static void OverrideEditor(Type componentType, Type editorType)
        {
            Type attributeType = Type.GetType("UnityEditor.CustomEditorAttributes, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            Type monoEditorType = Type.GetType("UnityEditor.CustomEditorAttributes+MonoEditorType, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            var editorsField = attributeType.GetField("kSCustomEditors", BindingFlags.Static | BindingFlags.NonPublic);
            var inspectorField = monoEditorType.GetField("m_InspectorType", BindingFlags.Public | BindingFlags.Instance);
            var editorDictionary = editorsField.GetValue(null) as IDictionary;
            var editorsList = editorDictionary[componentType] as IList;
            inspectorField.SetValue(editorsList[0], editorType);

            var inspectorType = Type.GetType("UnityEditor.InspectorWindow, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            var myTestMethod = inspectorType.GetMethod("RefreshInspectors", BindingFlags.NonPublic | BindingFlags.Static);
            myTestMethod.Invoke(null, null);
        }


        [InitializeOnLoadMethod]
        private static void DelayCallOverride()
        {
            EditorApplication.delayCall -= InitialOverride;
            EditorApplication.delayCall += InitialOverride;
        }

        private static void InitialOverride()
        {
            EditorApplication.delayCall -= InitialOverride;

            Type attributeType = Type.GetType("UnityEditor.CustomEditorAttributes, UnityEditor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            FieldInfo editorsInitializedField = attributeType.GetField("s_Initialized", BindingFlags.Static | BindingFlags.NonPublic);

            try
            {
                if (!(bool)editorsInitializedField.GetValue(null))
                {
                    MethodInfo rebuildEditorsMethod = attributeType.GetMethod("Rebuild", BindingFlags.Static | BindingFlags.NonPublic);
                    rebuildEditorsMethod.Invoke(null, null);
                    editorsInitializedField.SetValue(null, true);
                }

                OverrideEditor(typeof(VRCExpressionParameters), typeof(VRCParamsPlus));
                OverrideEditor(typeof(VRCExpressionsMenu), typeof(VRCMenuPlus));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                Debug.LogError("[VRCSDK+] Failed to override editors!");
            }

        }

        #endregion

        #region Quick Avatar
        [MenuItem("CONTEXT/VRCAvatarDescriptor/[SDK+] Quick Setup", false, 650)]
        private static void QuickSetup(MenuCommand command)
        {
            VRCAvatarDescriptor desc = (VRCAvatarDescriptor)command.context;
            Animator ani = desc.GetComponent<Animator>();
            SerializedObject serialized = new SerializedObject(desc);

            if (ani)
            {
                Transform leftEye = ani.GetBoneTransform(HumanBodyBones.LeftEye);
                Transform rightEye = ani.GetBoneTransform(HumanBodyBones.RightEye);

                Transform root = desc.transform;
                float worldXPosition;
                float worldYPosition;
                float worldZPosition;
                #region View Position
                if (leftEye && rightEye)
                {
                    Transform betterLeft = leftEye.parent.Find("LeftEye");
                    Transform betterRight = rightEye.parent.Find("RightEye");
                    leftEye = betterLeft ? betterLeft : leftEye;
                    rightEye = betterRight ? betterRight : rightEye;
                    var added = (leftEye.position + rightEye.position) / 2;
                    worldXPosition = added.x;
                    worldYPosition = added.y;
                    worldZPosition = added.z;
                }
                else
                {
                    Vector3 headPosition = ani.GetBoneTransform(HumanBodyBones.Head).position;
                    worldXPosition = headPosition.x;
                    worldYPosition = headPosition.y + ((headPosition.y - root.position.y) * 1.0357f - (headPosition.y - root.position.y));
                    worldZPosition = 0;
                }

                Vector3 realView = root.InverseTransformPoint(new Vector3(worldXPosition, worldYPosition, worldZPosition));
                realView = new Vector3(Mathf.Approximately(realView.x, 0) ? 0 : realView.x, realView.y, (realView.z + 0.0547f * realView.y) / 2);

                serialized.FindProperty("ViewPosition").vector3Value = realView;
                #endregion

                #region Eyes

                if (leftEye && rightEye)
                {
                    SerializedProperty eyes = serialized.FindProperty("customEyeLookSettings");
                    serialized.FindProperty("enableEyeLook").boolValue = true;

                    eyes.FindPropertyRelative("leftEye").objectReferenceValue = leftEye;
                    eyes.FindPropertyRelative("rightEye").objectReferenceValue = rightEye;

                    #region Rotation Values
                    const float axisValue = 0.1305262f;
                    const float wValue = 0.9914449f;

                    Quaternion upValue = new Quaternion(-axisValue, 0, 0, wValue);
                    Quaternion downValue = new Quaternion(axisValue, 0, 0, wValue);
                    Quaternion rightValue = new Quaternion(0, axisValue, 0, wValue);
                    Quaternion leftValue = new Quaternion(0, -axisValue, 0, wValue);

                    SerializedProperty up = eyes.FindPropertyRelative("eyesLookingUp");
                    SerializedProperty right = eyes.FindPropertyRelative("eyesLookingRight");
                    SerializedProperty down = eyes.FindPropertyRelative("eyesLookingDown");
                    SerializedProperty left = eyes.FindPropertyRelative("eyesLookingLeft");

                    void SetLeftAndRight(SerializedProperty p, Quaternion v)
                    {
                        p.FindPropertyRelative("left").quaternionValue = v;
                        p.FindPropertyRelative("right").quaternionValue = v;
                    }

                    SetLeftAndRight(up, upValue);
                    SetLeftAndRight(right, rightValue);
                    SetLeftAndRight(down, downValue);
                    SetLeftAndRight(left, leftValue);
                    #endregion

                    #region Blinking
                    SkinnedMeshRenderer body = null;
                    for (int i = 0; i < desc.transform.childCount; i++)
                    {
                        if (body = desc.transform.GetChild(i).GetComponent<SkinnedMeshRenderer>())
                            break;
                    }

                    if (body && body.sharedMesh)
                    {
                        for (int i = 0; i < body.sharedMesh.blendShapeCount; i++)
                        {
                            if (body.sharedMesh.GetBlendShapeName(i) != "Blink") continue;

                            eyes.FindPropertyRelative("eyelidType").enumValueIndex = 2;
                            eyes.FindPropertyRelative("eyelidsSkinnedMesh").objectReferenceValue = body;

                            SerializedProperty blendShapes = eyes.FindPropertyRelative("eyelidsBlendshapes");
                            blendShapes.arraySize = 3;
                            blendShapes.FindPropertyRelative("Array.data[0]").intValue = i;
                            blendShapes.FindPropertyRelative("Array.data[1]").intValue = -1;
                            blendShapes.FindPropertyRelative("Array.data[2]").intValue = -1;
                            break;
                        }
                    }
                    #endregion
                }
                #endregion
            }

            serialized.ApplyModifiedProperties();
            EditorApplication.delayCall -= ForceCallAutoLipSync;
            EditorApplication.delayCall += ForceCallAutoLipSync;
        }

        private static void ForceCallAutoLipSync()
        {
            EditorApplication.delayCall -= ForceCallAutoLipSync;

            var descriptorEditor = Type.GetType("AvatarDescriptorEditor3, Assembly-CSharp-Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null") ?? 
                                   Type.GetType("AvatarDescriptorEditor3, VRC.SDK3A.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");

            if (descriptorEditor == null)
            {
                Debug.LogWarning("AvatarDescriptorEditor3 Type couldn't be found!");
                return;
            }
            
            Editor tempEditor = (Editor)Resources.FindObjectsOfTypeAll(descriptorEditor)[0];
            descriptorEditor.GetMethod("AutoDetectLipSync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(tempEditor, null);
        }

        #endregion
    }

    internal static class VRCSDKPlusToolbox
    {
        #region Ready Paths
		internal enum PathOption
		{
			Normal,
			ForceFolder,
			ForceFile
		}
		internal static string ReadyAssetPath(string path, bool makeUnique = false, PathOption pathOption = PathOption.Normal)
		{
			bool forceFolder = pathOption == PathOption.ForceFolder;
			bool forceFile = pathOption == PathOption.ForceFile;

			path = forceFile ? LegalizeName(path) : forceFolder ? LegalizePath(path) : LegalizeFullPath(path);
			bool isFolder = forceFolder || (!forceFile && string.IsNullOrEmpty(Path.GetExtension(path)));

			if (isFolder)
			{
				if (!Directory.Exists(path))
				{
					Directory.CreateDirectory(path);
					AssetDatabase.ImportAsset(path);
				}
				else if (makeUnique)
				{
					path = AssetDatabase.GenerateUniqueAssetPath(path);
					Directory.CreateDirectory(path);
					AssetDatabase.ImportAsset(path);
				}
			}
			else
			{
				const string basePath = "Assets";
				string folderPath = Path.GetDirectoryName(path);
				string fileName = Path.GetFileName(path);

				if (string.IsNullOrEmpty(folderPath))
					folderPath = basePath;
				else if (!folderPath.StartsWith(Application.dataPath) && !folderPath.StartsWith(basePath))
					folderPath = $"{basePath}/{folderPath}";

				if (folderPath != basePath && !Directory.Exists(folderPath))
				{
					Directory.CreateDirectory(folderPath);
					AssetDatabase.ImportAsset(folderPath);
				}

				path = $"{folderPath}/{fileName}";
				if (makeUnique)
					path = AssetDatabase.GenerateUniqueAssetPath(path);

			}

			return path;
		}

		internal static string ReadyAssetPath(string folderPath, string fullNameOrExtension, bool makeUnique = false)
		{
			if (string.IsNullOrEmpty(fullNameOrExtension))
				return ReadyAssetPath(LegalizePath(folderPath), makeUnique, PathOption.ForceFolder);
			if (string.IsNullOrEmpty(folderPath))
				return ReadyAssetPath(LegalizeName(fullNameOrExtension), makeUnique, PathOption.ForceFile);

			return ReadyAssetPath($"{LegalizePath(folderPath)}/{LegalizeName(fullNameOrExtension)}", makeUnique);
		}
		internal static string ReadyAssetPath(Object buddyAsset, string fullNameOrExtension = "", bool makeUnique = true)
		{
			var buddyPath = AssetDatabase.GetAssetPath(buddyAsset);
			string folderPath = Path.GetDirectoryName(buddyPath);
			if (string.IsNullOrEmpty(fullNameOrExtension))
				fullNameOrExtension = Path.GetFileName(buddyPath);
			if (fullNameOrExtension.StartsWith("."))
			{
				string assetName = string.IsNullOrWhiteSpace(buddyAsset.name) ? "SomeAsset" : buddyAsset.name;
				fullNameOrExtension = $"{assetName}{fullNameOrExtension}";
			}

			return ReadyAssetPath(folderPath, fullNameOrExtension, makeUnique);
		}

		internal static string LegalizeFullPath(string path)
		{
			if (string.IsNullOrEmpty(path))
			{
				Debug.LogWarning("Legalizing empty path! Returned path as 'EmptyPath'");
				return "EmptyPath";
			}

			var ext = Path.GetExtension(path);
			bool isFolder = string.IsNullOrEmpty(ext);
			if (isFolder) return LegalizePath(path);

			string folderPath = Path.GetDirectoryName(path);
			var fileName = LegalizeName(Path.GetFileNameWithoutExtension(path));

			if (string.IsNullOrEmpty(folderPath)) return $"{fileName}{ext}";
			folderPath = LegalizePath(folderPath);

			return $"{folderPath}/{fileName}{ext}";
		}
		internal static string LegalizePath(string path)
		{
			string regexFolderReplace = Regex.Escape(new string(Path.GetInvalidPathChars()));

			path = path.Replace('\\', '/');
			if (path.IndexOf('/') > 0)
				path = string.Join("/", path.Split('/').Select(s => Regex.Replace(s, $@"[{regexFolderReplace}]", "-")));

			return path;

		}
		internal static string LegalizeName(string name)
		{
			string regexFileReplace = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
			return string.IsNullOrEmpty(name) ? "Unnamed" : Regex.Replace(name, $@"[{regexFileReplace}]", "-");
		}
		#endregion
        
        internal static bool TryGetActiveIndex(this ReorderableList orderList, out int index)
        {
            index = orderList.index;
            if (index < orderList.count && index >= 0) return true;
            index = -1;
            return false;
        }
        public static string GenerateUniqueString(string s, Func<string, bool> PassCondition, bool addNumberIfMissing = true)
        {
            if (PassCondition(s)) return s;
            var match = Regex.Match(s, @"(?=.*)(\d+)$");
            if (!match.Success && !addNumberIfMissing) return s;
            var numberString = match.Success ? match.Groups[1].Value : "1";
            if (!match.Success && !s.EndsWith(" ")) s += " ";
            var newString = Regex.Replace(s, @"(?=.*?)\d+$", string.Empty);
            while (!PassCondition($"{newString}{numberString}")) 
                numberString = (int.Parse(numberString) + 1).ToString(new string('0', numberString.Length));
            
            return $"{newString}{numberString}";
        }
        public static class Container
        {
            public class Vertical : IDisposable
            {
                public Vertical(params GUILayoutOption[] options)
                    => EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

                public Vertical(string title, params GUILayoutOption[] options)
                {
                    EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

                    EditorGUILayout.LabelField(title, VRCSDKPlusToolbox.Styles.Label.Centered);
                }

                public void Dispose() => EditorGUILayout.EndVertical();
            }
            public class Horizontal : IDisposable
            {
                public Horizontal(params GUILayoutOption[] options)
                    => EditorGUILayout.BeginHorizontal(GUI.skin.GetStyle("helpbox"), options);

                public Horizontal(string title, params GUILayoutOption[] options)
                {
                    EditorGUILayout.BeginHorizontal(GUI.skin.GetStyle("helpbox"), options);

                    EditorGUILayout.LabelField(title, VRCSDKPlusToolbox.Styles.Label.Centered);
                }

                public void Dispose() => EditorGUILayout.EndHorizontal();
            }

            public static void BeginLayout(params GUILayoutOption[] options)
                => EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

            public static void BeginLayout(string title, params GUILayoutOption[] options)
            {
                EditorGUILayout.BeginVertical(GUI.skin.GetStyle("helpbox"), options);

                EditorGUILayout.LabelField(title, VRCSDKPlusToolbox.Styles.Label.Centered);
            }
            public static void EndLayout() => EditorGUILayout.EndVertical();

            public static Rect GUIBox(float height)
            {
                var rect = EditorGUILayout.GetControlRect(false, height);
                return GUIBox(ref rect);
            }

            public static Rect GUIBox(ref Rect rect)
            {
                GUI.Box(rect, "", GUI.skin.GetStyle("helpbox"));

                rect.x += 4;
                rect.width -= 8;
                rect.y += 3;
                rect.height -= 6;

                return rect;
            }
        }

        public static class Placeholder
        {

            public static void GUILayout(float height) =>
                GUI(EditorGUILayout.GetControlRect(false, height));

            public static void GUI(Rect rect) => GUI(rect, EditorGUIUtility.isProSkin ? 53 : 182);

            private static void GUI(Rect rect, float color)
            {
                EditorGUI.DrawTextureTransparent(rect, GetColorTexture(color));
            }
        }

        public static class Styles
        {
            public const float Padding = 3;

            public static class Label
            {
                internal static readonly UnityEngine.GUIStyle Centered
                    = new UnityEngine.GUIStyle(GUI.skin.label) {alignment = TextAnchor.MiddleCenter};

                internal static readonly UnityEngine.GUIStyle RichText
                    = new UnityEngine.GUIStyle(GUI.skin.label) {richText = true};
                

                internal static readonly UnityEngine.GUIStyle Type
                    = new UnityEngine.GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleRight,
                        normal =
                        {
                            textColor = EditorGUIUtility.isProSkin ? Color.gray : BrightnessToColor(91),
                        },
                        fontStyle = FontStyle.Italic,
                    };

                internal static readonly UnityEngine.GUIStyle PlaceHolder
                    = new UnityEngine.GUIStyle(Type)
                    {
                        fontSize = 11,
                        alignment = TextAnchor.MiddleLeft,
                        contentOffset = new Vector2(2.5f, 0)
                    };
                internal static readonly GUIStyle faintLinkLabel = new GUIStyle(PlaceHolder) { name = "Toggle", hover = { textColor = new Color(0.3f, 0.7f, 1) } };

                internal static readonly UnityEngine.GUIStyle TypeFocused
                    = new UnityEngine.GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleRight,
                        normal =
                        {
                            textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black,
                        },
                        fontStyle = FontStyle.Italic,
                    };

                internal static readonly GUIStyle TypeLabel = new GUIStyle(PlaceHolder) {contentOffset = new Vector2(-2.5f, 0)};
                internal static readonly GUIStyle RightPlaceHolder = new GUIStyle(TypeLabel) {alignment = TextAnchor.MiddleRight};
                internal static readonly UnityEngine.GUIStyle Watermark
                    = new UnityEngine.GUIStyle(PlaceHolder)
                    {
                        alignment = TextAnchor.MiddleRight,
                        fontSize  = 10,
                    };

                internal static readonly UnityEngine.GUIStyle LabelDropdown
                    = new UnityEngine.GUIStyle(GUI.skin.GetStyle("DropDownButton"))
                    {
                        alignment = TextAnchor.MiddleLeft,
                        contentOffset = new Vector2(2.5f, 0)
                    };

                internal static readonly UnityEngine.GUIStyle RemoveIcon
                    = new UnityEngine.GUIStyle(GUI.skin.GetStyle("RL FooterButton"));

            }

            internal static readonly GUIStyle icon = new GUIStyle(GUI.skin.label) {fixedWidth = 18, fixedHeight = 18};
            internal static readonly UnityEngine.GUIStyle letterButton = 
                new UnityEngine.GUIStyle(GUI.skin.button) { padding = new RectOffset(), margin = new RectOffset(1,1,1,1), richText = true};

        }

        public static class Strings
        {
            public const string IconCopy = "SaveActive";
            public const string IconPaste = "Clipboard";
            public const string IconMove = "MoveTool";
            public const string IconPlace = "DefaultSorting";
            public const string IconDuplicate = "TreeEditor.Duplicate";
            public const string IconHelp = "_Help";
            public const string IconWarn = "console.warnicon.sml";
            public const string IconError = "console.erroricon.sml";
            public const string IconClear = "winbtn_win_close";
            public const string IconFolder = "FolderOpened Icon";
            public const string IconRemove = "Toolbar Minus";
            public const string IconSearch = "Search Icon";

            public const string ClipboardPrefixControl = "[TAG=VSP_CONTROL]";

            public const string SettingsCompact = "VSP_Compact";
        }

        public static class GUIContent
        {
            public const string MissingParametersTooltip = "No Expression Parameters targeted. Auto-fill and warnings are disabled.";
            public const string MenuFullTooltip = "Menu's controls are already maxed out. (8/8)";
            public static readonly UnityEngine.GUIContent Copy
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconCopy))
                {
                    tooltip = "Copy"
                };

            public static readonly UnityEngine.GUIContent Paste
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconPaste))
                {
                    tooltip = "Paste"
                };

            public static readonly UnityEngine.GUIContent Move
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconMove))
                {
                    tooltip = "Move"
                };
            public static readonly UnityEngine.GUIContent Place
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconPlace))
                {
                    tooltip = "Place"
                };

            public static readonly UnityEngine.GUIContent Duplicate
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconDuplicate))
                {
                    tooltip = "Duplicate"
                };

            public static readonly UnityEngine.GUIContent Help
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconHelp));

            public static readonly UnityEngine.GUIContent Warn
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconWarn));
            public static readonly UnityEngine.GUIContent Error
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconError));

            public static readonly UnityEngine.GUIContent Clear
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconClear))
                {
                    tooltip = "Clear"
                };

            public static readonly UnityEngine.GUIContent Folder
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconFolder))
                {
                    tooltip = "Open"
                };

            public static readonly UnityEngine.GUIContent Remove
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconRemove)) {tooltip = "Remove element from list"};

            public static readonly UnityEngine.GUIContent Search
                = new UnityEngine.GUIContent(EditorGUIUtility.IconContent(VRCSDKPlusToolbox.Strings.IconSearch)) {tooltip = "Search"};
        }
        
        public static class Preferences
        {
            public static bool CompactMode
            {
                get => EditorPrefs.GetBool(VRCSDKPlusToolbox.Strings.SettingsCompact, false);
                set => EditorPrefs.SetBool(VRCSDKPlusToolbox.Strings.SettingsCompact, value);
            }
        }

        public static Color BrightnessToColor(float brightness)
        {
            if (brightness > 1) brightness /= 255;
            return new Color(brightness, brightness, brightness, 1);
        }
        private static readonly Texture2D tempTexture = new Texture2D(1, 1) { anisoLevel = 0, filterMode = FilterMode.Point };
        internal static Texture2D GetColorTexture(float rgb, float a = 1)
            => GetColorTexture(rgb, rgb, rgb, a);

        internal static Texture2D GetColorTexture(float r, float g, float b, float a = 1)
        {
            if (r > 1) r /= 255;
            if (g > 1) g /= 255;
            if (b > 1) b /= 255;
            if (a > 1) a /= 255;

            return GetColorTexture(new Color(r, g, b, a));
        }
        internal static Texture2D GetColorTexture(Color color)
        {
            tempTexture.SetPixel(0, 0, color);
            tempTexture.Apply();
            return tempTexture;
        }

        // ReSharper disable once InconsistentNaming
        public static VRCExpressionsMenu.Control.ControlType ToControlType(this SerializedProperty property)
        {
            var value = property.enumValueIndex;
            switch (value)
            {
                case 0:
                    return VRCExpressionsMenu.Control.ControlType.Button;
                case 1:
                    return VRCExpressionsMenu.Control.ControlType.Toggle;
                case 2:
                    return VRCExpressionsMenu.Control.ControlType.SubMenu;
                case 3:
                    return VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet;
                case 4:
                    return VRCExpressionsMenu.Control.ControlType.FourAxisPuppet;
                case 5:
                    return VRCExpressionsMenu.Control.ControlType.RadialPuppet;
            }

            return VRCExpressionsMenu.Control.ControlType.Button;
        }

        public static int GetEnumValueIndex(this VRCExpressionsMenu.Control.ControlType type)
        {
            switch (type)
            {
                case VRCExpressionsMenu.Control.ControlType.Button:
                    return 0;
                case VRCExpressionsMenu.Control.ControlType.Toggle:
                    return 1;
                case VRCExpressionsMenu.Control.ControlType.SubMenu:
                    return 2;
                case VRCExpressionsMenu.Control.ControlType.TwoAxisPuppet:
                    return 3;
                case VRCExpressionsMenu.Control.ControlType.FourAxisPuppet:
                    return 4;
                case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                    return 5;
                default:
                    return -1;
            }
        }

        public static int FindIndex(this IEnumerable array, object target)
        {
            var enumerator = array.GetEnumerator();
            var index = 0;
            while (enumerator.MoveNext())
            {
                if (enumerator.Current != null && enumerator.Current.Equals(target))
                    return index;
                index++;
            }

            return -1;
        }
        internal static bool GetPlayableLayer(this VRCAvatarDescriptor avi, VRCAvatarDescriptor.AnimLayerType type, out AnimatorController controller)
        {
            controller = (from l in avi.baseAnimationLayers.Concat(avi.specialAnimationLayers) where l.type == type select l.animatorController).FirstOrDefault() as AnimatorController;
            return controller != null;
        }

        internal static bool IterateArray(this SerializedProperty property, Func<int, SerializedProperty, bool> func, params int[] skipIndex)
        {
            for (int i = property.arraySize - 1; i >= 0; i--)
            {
                if (skipIndex.Contains(i)) continue;
                if (i >= property.arraySize) continue;
                if (func(i, property.GetArrayElementAtIndex(i)))
                    return true;
            }
            return false;
        }

        #region Keyboard Commands
        internal enum EventCommands
        {
            Copy,
            Cut,
            Paste,
            Duplicate,
            Delete,
            SoftDelete,
            SelectAll,
            Find,
            FrameSelected,
            FrameSelectedWithLock,
            FocusProjectWindow
        }
        internal static bool HasReceivedCommand(EventCommands command, string matchFocusControl = "", bool useEvent = true)
        {
            if (!string.IsNullOrEmpty(matchFocusControl) && GUI.GetNameOfFocusedControl() != matchFocusControl) return false;
            Event e = Event.current;
            if (e.type != EventType.ValidateCommand) return false;
            bool received = command.ToString() == e.commandName;
            if (received && useEvent) e.Use();
            return received;
        }

        internal static bool HasReceivedKey(KeyCode key, string matchFocusControl = "", bool useEvent = true)
        {
            if (!string.IsNullOrEmpty(matchFocusControl) && GUI.GetNameOfFocusedControl() != matchFocusControl) return false;
            Event e = Event.current;
            bool received = e.type == EventType.KeyDown && e.keyCode == key;
            if (received && useEvent) e.Use();
            return received;
        }

        internal static bool HasReceivedEnter(string matchFocusControl = "",bool useEvent = true) => HasReceivedKey(KeyCode.Return, matchFocusControl, useEvent) || HasReceivedKey(KeyCode.KeypadEnter, matchFocusControl, useEvent);
        internal static bool HasReceivedCancel(string matchFocusControl = "",  bool useEvent = true) => HasReceivedKey(KeyCode.Escape, matchFocusControl, useEvent);
        internal static bool HasReceivedAnyDelete(string matchFocusControl = "", bool useEvent = true) => HasReceivedCommand(EventCommands.SoftDelete, matchFocusControl, useEvent) || HasReceivedCommand(EventCommands.Delete, matchFocusControl, useEvent) || HasReceivedKey(KeyCode.Delete, matchFocusControl, useEvent);
        internal static bool HandleConfirmEvents(string matchFocusControl = "", Action onConfirm = null, Action onCancel = null)
        {
            if (HasReceivedEnter(matchFocusControl))
            {
                onConfirm?.Invoke();
                return true;
            }

            if (HasReceivedCancel(matchFocusControl))
            {
                onCancel?.Invoke();
                return true;
            }
            return false;
        }

        internal static bool HandleTextFocusConfirmCommands(string matchFocusControl, Action onConfirm = null, Action onCancel = null)
        {
            if (!HandleConfirmEvents(matchFocusControl, onConfirm, onCancel)) return false;
            GUI.FocusControl(null);
            return true;
        }
        #endregion

        internal abstract class CustomDropdownBase : PopupWindowContent
        {
            internal static readonly GUIStyle backgroundStyle = new GUIStyle()
            {
                hover = { background = VRCSDKPlusToolbox.GetColorTexture(new Color(0.3020f, 0.3020f, 0.3020f)) },
                active = { background = VRCSDKPlusToolbox.GetColorTexture(new Color(0.1725f, 0.3647f, 0.5294f)) }
            };

            internal static readonly GUIStyle titleStyle = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.Bold
            };

        }
        internal class CustomDropdown<T> : CustomDropdownBase
        {

            private readonly string title;
            private string search;
            internal DropDownItem[] items;
            private readonly Action<DropDownItem> itemGUI;
            private readonly Action<int, T> onSelected;
            private Func<T, string, bool> onSearchChanged;

            private bool hasSearch;
            private float width;
            private bool firstPass = true;
            private Vector2 scroll;
            private readonly Rect[] selectionRects;

            public CustomDropdown(string title, IEnumerable<T> itemArray, Action<DropDownItem> itemGUI, Action<int, T> onSelected)
            {
                this.title = title;
                this.onSelected = onSelected;
                this.itemGUI = itemGUI;
                items = itemArray.Select((item, i) => new DropDownItem(item, i)).ToArray();
                selectionRects = new Rect[items.Length];
            }

            public void EnableSearch(Func<T, string, bool> onSearchChanged)
            {
                hasSearch = true;
                this.onSearchChanged = onSearchChanged;
            }

            public void OrderBy(Func<T, object> orderFunc)
            {
                items = orderFunc != null ? items.OrderBy(item => orderFunc(item.value)).ToArray() : items;
            }

            public void SetExtraOptions(Func<T, object[]> argReturn)
            {
                foreach (var i in items)
                    i.args = argReturn(i.value);
            }

            public override void OnGUI(Rect rect)
            {

                using (new GUILayout.AreaScope(rect))
                {
                    var e = Event.current;
                    scroll = GUILayout.BeginScrollView(scroll);
                    if (!string.IsNullOrEmpty(title))
                    {
                        GUILayout.Label(title, titleStyle);
                        DrawSeparator();
                    }
                    if (hasSearch)
                    {
                        EditorGUI.BeginChangeCheck();
                        if (firstPass) GUI.SetNextControlName($"{title}SearchBar");
                        search = EditorGUILayout.TextField(search, GUI.skin.GetStyle("SearchTextField"));
                        if (EditorGUI.EndChangeCheck())
                        {
                            foreach (var i in items)
                                i.displayed = onSearchChanged(i.value, search);
                        }
                    }

                    var t = e.type;
                    for (int i = 0; i < items.Length; i++)
                    {
                        var item = items[i];
                        if (!item.displayed) continue;
                        if (!firstPass)
                        {
                            if (GUI.Button(selectionRects[i], string.Empty, backgroundStyle))
                            {
                                onSelected(item.itemIndex, item.value);
                                editorWindow.Close();
                            }
                        }
                        using (new GUILayout.VerticalScope()) itemGUI(item);

                        if (t == EventType.Repaint)
                        {
                            selectionRects[i] = GUILayoutUtility.GetLastRect();

                            if (firstPass && selectionRects[i].width > width)
                                width = selectionRects[i].width;
                        }
                    }

                    if (t == EventType.Repaint && firstPass)
                    {
                        firstPass = false;
                        GUI.FocusControl($"{title}SearchBar");
                    }
                    GUILayout.EndScrollView();
                    if (rect.Contains(e.mousePosition))
                        editorWindow.Repaint();
                }
            }

            public override Vector2 GetWindowSize()
            {
                Vector2 ogSize = base.GetWindowSize();
                if (!firstPass) ogSize.x = width + 21;
                return ogSize;
            }

            public void Show(Rect position) => PopupWindow.Show(position, this);
            internal class DropDownItem
            {
                internal readonly int itemIndex;
                internal readonly T value;

                internal object[] args;
                internal bool displayed = true;

                internal object extra
                {
                    get => args[0];
                    set => args[0] = value;
                }

                internal DropDownItem(T value, int itemIndex)
                {
                    this.value = value;
                    this.itemIndex = itemIndex;
                }

                public static implicit operator T(DropDownItem i) => i.value;
            }

            private static void DrawSeparator(int thickness = 2, int padding = 10)
            {
                Rect r = EditorGUILayout.GetControlRect(GUILayout.Height(thickness + padding));
                r.height = thickness;
                r.y += padding / 2f;
                r.x -= 2;
                r.width += 6;
                ColorUtility.TryParseHtmlString(EditorGUIUtility.isProSkin ? "#595959" : "#858585", out Color lineColor);
                EditorGUI.DrawRect(r, lineColor);
            }

        }

    }



}