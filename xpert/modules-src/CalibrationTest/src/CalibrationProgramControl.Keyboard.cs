using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace HVX.Controls
{
    public sealed partial class CalibrationProgramControl
    {
        string calibrationKeyboardAction = "";
        sealed class CalibrationUiAction
        {
            internal string Id, Caption, Description;
            internal Rectangle Bounds;
            internal bool Enabled;
        }
        Rectangle CalibrationStartBounds
        { get { return UseCompactLayout ? Rectangle.Round(CompactLayout.Start) : ReferenceRectangleToClient(new RectangleF(1398, 796, 243, 94)); } }
        bool CalibrationNumericEditorVisible { get { return numericEditor != null && numericEditor.Visible; } }

        // These virtual controls share the visible hit regions. Activation enters
        // OnMouseUp, so it cannot bypass its acquisition, busy or parameter gates.
        List<CalibrationUiAction> CalibrationKeyboardActions()
        {
            var actions = new List<CalibrationUiAction>();
            bool available = Enabled && !IsDisposed && !Disposing && !jointDialogOpen && !jointConfirmationOpen;
            Action<string,string,RectangleF,bool> add = (id,caption,bounds,enabled) => {
                Rectangle rect = UseCompactLayout ? Rectangle.Round(bounds) : ReferenceRectangleToClient(bounds);
                rect.Intersect(ClientRectangle);
                if (rect.Width > 0 && rect.Height > 0) actions.Add(new CalibrationUiAction { Id=id, Caption=caption,
                    Description=id=="level"?CalibrationText("按回车选择档位；左右方向键切换档位。","Press Enter to choose a level; Left/Right change levels."):caption,
                    Bounds=rect, Enabled=available&&enabled });
            };
            bool idle = !IsBusy;
            if (UseCompactLayout)
            {
                var ui = CompactLayout;
                if (compactLevelsOpen && idle)
                {
                    string[] levels = GetVisibleLevelCodes();
                    for (int i=0;i<levels.Length;i++) add("choose-level:"+levels[i],levels[i],ui.PopupRow(i,levels.Length),idle);
                    return actions;
                }
                if (!IsCapacitiveLoad) {
                    add("length",CalibrationText("电缆长度","Cable length"),ui.LengthValue,idle);
                    add("velocity",CalibrationText("传播波速","Propagation velocity"),ui.VelocityValue,idle);
                    add("velocity-review",CalibrationText("核对本相波速","Review phase velocity"),ui.VelocityReview,idle);
                    float offset=EmbeddedMode?-40:0;
                    add("short-cable",CalibrationText("短电缆","Short cable"),new RectangleF(160,219+offset,54,34),idle);
                }
                add("manual-bandwidth",CalibrationText("显示手动调节","Show manual controls"),new RectangleF(160,255+(EmbeddedMode?-40:0),54,34),idle);
                add("waveform",CalibrationWaveformCaption,ui.TdrTab,idle);
                add("quality",CalibrationText("噪声评估","Noise evaluation"),ui.QualityTab,idle);
                if(compactQualityTab) {
                    add("noise-zoom",CalibrationText("拖动放大","Drag to zoom"),NoiseModeBounds(ui,0),idle);
                    add("noise-select",CalibrationText("选取干扰","Select interference"),NoiseModeBounds(ui,1),idle);
                    add("noise-auto",CalibrationText("自动阈值","Automatic threshold"),NoiseModeBounds(ui,2),idle);
                    add("noise-reset",CalibrationText("恢复原始视图","Reset view"),NoiseResetBounds(ui),idle);
                    add("noise-threshold",NoiseThresholdCaption,NoiseThresholdCheckBounds(ui),idle);
                }
                if (!compactQualityTab) {
                    if (!IsCapacitiveLoad) add("show-joints",CalibrationText("显示接头","Show joints"),JointVisibilityBounds,idle);
                    add("segment",CalibrationText("段采集","Segment capture"),ui.SegmentMode,idle);
                    add("pulse",CalibrationText("脉冲采集","Pulse capture"),ui.PulseMode,idle);
                    add("assessment",CalibrationText("校准评估","Calibration assessment"),ui.Assessment,idle&&SelectedCalibrationHasData);
                }
                for (int i=0;i<4;i++) add("phase:"+i,CalibrationText("相别 ","Phase ")+(i==0?"ALL":((char)('A'+i-1)).ToString()),ui.Phase(i),idle);
                add("level",CalibrationText("校准档位 ","Calibration level ")+SelectedLevelCode,ui.Level,idle);
                add("scope",CalibrationText("显示全部档位","Show all levels"),ui.AllLevels,idle);
                RectangleF levelArea=CompactLevelListBounds(ui);string[] visibleLevels=GetVisibleLevelCodes();
                int levelRows=CompactLevelRows(levelArea);
                for(int i=0;i<levelRows&&compactLevelOffset+i<visibleLevels.Length;i++) {
                    string code=visibleLevels[compactLevelOffset+i];
                    add("choose-level:"+code,code+" "+LevelSaveStateCaption(GetLevelSaveState(SelectedPhaseIndex,code)),
                        new RectangleF(levelArea.Left+2,levelArea.Top+2+i*28,levelArea.Width-18,27),idle);
                }
                if (!IsCapacitiveLoad) add("joints",CalibrationText("确认接头","Review joints"),ui.ConfirmJoints,idle);
                add("save",CalibrationSaveCaption,ui.Save,idle&&SelectedCalibrationHasData);
                add("start",IsBusy?CalibrationBusyCaption:CalibrationStartCaption,ui.Start,idle||CanStopCalibration);
            }
            else
            {
                if (!IsCapacitiveLoad) {
                    add("length",CalibrationText("电缆长度","Cable length"),new RectangleF(37,183,75,37),idle);
                    add("velocity",CalibrationText("传播波速","Propagation velocity"),new RectangleF(37,280,75,37),idle);
                }
                for(int i=0;i<4;i++)add("phase:"+i,CalibrationText("相别 ","Phase ")+(i==0?"ALL":((char)('A'+i-1)).ToString()),new RectangleF(1399+i*60.75f,121,60.75f,44),idle);
                if (!IsCapacitiveLoad) add("show-joints",CalibrationText("显示接头","Show joints"),JointVisibilityBounds,idle);
                add("scope",CalibrationText("显示全部档位","Show all levels"),new RectangleF(1397,261,245,39),idle);
                string[] levels=GetVisibleLevelCodes();
                for(int i=0;i<levels.Length;i++)add("choose-level:"+levels[i],levels[i],new RectangleF(1397,300+i*26.8f,245,26.8f),idle);
                add("save",CalibrationSaveCaption,new RectangleF(1397,733,246,52),idle&&SelectedCalibrationHasData);
                add("start",IsBusy?CalibrationBusyCaption:CalibrationStartCaption,new RectangleF(1398,796,243,94),idle||CanStopCalibration);
            }
            return actions;
        }
        void FocusCalibrationAction(string id)
        {
            var actions=CalibrationKeyboardActions(); var action=actions.FirstOrDefault(a=>a.Id==id&&a.Enabled);
            if(action==null)return;
            calibrationKeyboardAction=id; Focus(); Invalidate();
            AccessibilityNotifyClients(AccessibleEvents.Focus,actions.IndexOf(action)+(CalibrationNumericEditorVisible?1:0));
        }
        void ActivateCalibrationAction(string id)
        {
            var action=CalibrationKeyboardActions().FirstOrDefault(a=>a.Id==id&&a.Enabled);
            if(action==null)return;
            var r=action.Bounds;
            OnMouseUp(new MouseEventArgs(MouseButtons.Left,1,r.Left+r.Width/2,r.Top+r.Height/2,0));
            if(compactLevelsOpen)calibrationKeyboardAction="choose-level:"+SelectedLevelCode;
            else if(id.StartsWith("choose-level:",StringComparison.Ordinal))calibrationKeyboardAction=UseCompactLayout?"level":"choose-level:"+SelectedLevelCode;
            Invalidate(); AccessibilityNotifyClients(AccessibleEvents.StateChange,-1);
        }
        bool HandleCalibrationKeyboardKey(Keys key, bool repeated)
        {
            if(!Focused || numericEditor!=null&&numericEditor.Visible)return false;
            if(key==Keys.Escape&&compactLevelsOpen){compactLevelsOpen=false;calibrationKeyboardAction="level";Invalidate();return true;}
            var actions=CalibrationKeyboardActions().Where(a=>a.Enabled).ToList();
            if(key==Keys.Tab||key==(Keys.Tab|Keys.Shift))
            {
                int at=actions.FindIndex(a=>a.Id==calibrationKeyboardAction),next=at+(key==Keys.Tab?1:-1);
                if(at<0)next=key==Keys.Tab?0:actions.Count-1;
                if(next>=0&&next<actions.Count){FocusCalibrationAction(actions[next].Id);return true;}
                calibrationKeyboardAction="";compactLevelsOpen=false;Invalidate();return false;
            }
            if(key==Keys.Enter||key==Keys.Space)
            {
                // WM_KEYDOWN's previous-key-state bit distinguishes auto-repeat
                // from a new intentional press after the start button becomes Stop.
                if(repeated)return true;
                if(calibrationKeyboardAction.Length>0){ActivateCalibrationAction(calibrationKeyboardAction);return true;}return false;
            }
            if((key==Keys.Left||key==Keys.Right)&&calibrationKeyboardAction=="level"&&actions.Any(a=>a.Id=="level"))
            {
                string[] levels=GetVisibleLevelCodes();int index=Array.IndexOf(levels,SelectedLevelCode);
                int next=Math.Max(0,Math.Min(levels.Length-1,index+(key==Keys.Right?1:-1)));
                if(next!=index)ExecuteParameterAction("LEVEL",delegate{SetSelectedLevel(levels[next]);});
                Invalidate();return true;
            }
            return false;
        }
        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            var actions=CalibrationKeyboardActions().Where(a=>a.Enabled).ToList();
            if(calibrationKeyboardAction.Length==0&&actions.Count>0)calibrationKeyboardAction=(ModifierKeys&Keys.Shift)!=0?actions[actions.Count-1].Id:actions[0].Id;
            Invalidate();
        }
        protected override void OnGotFocus(EventArgs e){base.OnGotFocus(e);Invalidate();}
        protected override void OnLostFocus(EventArgs e){base.OnLostFocus(e);Invalidate();}
        void PaintCalibrationKeyboardFocus(Graphics graphics)
        {
            if(!Focused)return;
            var action=CalibrationKeyboardActions().FirstOrDefault(a=>a.Id==calibrationKeyboardAction&&a.Enabled);
            if(action==null)return;
            Rectangle r=action.Bounds;r.Inflate(-3,-3);
            ControlPaint.DrawFocusRectangle(graphics,r,Color.Black,Color.White);
        }
        protected override AccessibleObject CreateAccessibilityInstance(){return new CalibrationAccessibleObject(this);}
        sealed class CalibrationAccessibleObject : ControlAccessibleObject
        {
            readonly CalibrationProgramControl page;
            internal CalibrationAccessibleObject(CalibrationProgramControl page):base(page){this.page=page;}
            public override AccessibleRole Role{get{return AccessibleRole.Pane;}}
            public override string Name{get{return page.CalibrationText("校准测试","Calibration test");}set{base.Name=value;}}
            public override int GetChildCount(){return page.CalibrationKeyboardActions().Count+(page.CalibrationNumericEditorVisible?1:0);}
            public override AccessibleObject GetChild(int index)
            {
                if(page.CalibrationNumericEditorVisible){if(index==0)return page.numericEditor.AccessibilityObject;index--;}
                var a=page.CalibrationKeyboardActions();return index<0||index>=a.Count?null:new CalibrationActionAccessibleObject(page,this,a[index].Id);
            }
            public override AccessibleObject GetFocused()
            {
                if(page.CalibrationNumericEditorVisible&&page.numericEditor.ContainsFocus)return page.numericEditor.AccessibilityObject;
                return page.Focused&&page.calibrationKeyboardAction.Length>0?new CalibrationActionAccessibleObject(page,this,page.calibrationKeyboardAction):base.GetFocused();
            }
            public override AccessibleObject HitTest(int x,int y)
            {
                var point=page.PointToClient(new Point(x,y));
                if(page.CalibrationNumericEditorVisible&&page.numericEditor.Bounds.Contains(point))return page.numericEditor.AccessibilityObject;
                var action=page.CalibrationKeyboardActions().FirstOrDefault(a=>a.Bounds.Contains(point));
                return action==null?base.HitTest(x,y):new CalibrationActionAccessibleObject(page,this,action.Id);
            }
        }
        sealed class CalibrationActionAccessibleObject : AccessibleObject
        {
            readonly CalibrationProgramControl page;readonly AccessibleObject parent;readonly string id;
            internal CalibrationActionAccessibleObject(CalibrationProgramControl page,AccessibleObject parent,string id){this.page=page;this.parent=parent;this.id=id;}
            CalibrationUiAction Action{get{return page.CalibrationKeyboardActions().FirstOrDefault(a=>a.Id==id);}}
            public override AccessibleObject Parent{get{return parent;}}
            public override AccessibleRole Role{get{return id=="scope"||id=="show-joints"||id=="short-cable"||id=="manual-bandwidth"?AccessibleRole.CheckButton:id.StartsWith("phase:",StringComparison.Ordinal)?AccessibleRole.RadioButton:AccessibleRole.PushButton;}}
            public override string Name{get{var a=Action;return a==null?"":a.Caption;}set{}}
            public override string Description{get{var a=Action;return a==null?"":a.Description;}}
            public override string DefaultAction{get{return page.CalibrationText("激活","Activate");}}
            public override Rectangle Bounds{get{var a=Action;return a==null||!page.IsHandleCreated?Rectangle.Empty:page.RectangleToScreen(a.Bounds);}}
            public override AccessibleStates State{get{
                var a=Action;if(a==null)return AccessibleStates.Invisible;
                bool selected=id=="scope"&&page.ShowAllLevels||id=="show-joints"&&page.JointMarkersVisible||id=="short-cable"&&page.ShortCable||id=="manual-bandwidth"&&page.ManualBandwidthPanelVisible||id=="phase:"+(page.SelectedPhaseIndex+1);
                return AccessibleStates.Focusable|(selected?AccessibleStates.Checked:AccessibleStates.None)|(a.Enabled?AccessibleStates.None:AccessibleStates.Unavailable)|(page.Focused&&page.calibrationKeyboardAction==id?AccessibleStates.Focused:AccessibleStates.None);
            }}
            public override void DoDefaultAction(){page.RunOnUiThread(delegate{page.ActivateCalibrationAction(id);});}
            public override void Select(AccessibleSelection flags){if((flags&AccessibleSelection.TakeFocus)!=0)page.RunOnUiThread(delegate{page.FocusCalibrationAction(id);});}
            public override AccessibleObject Navigate(AccessibleNavigation direction)
            {
                if(direction!=AccessibleNavigation.Next&&direction!=AccessibleNavigation.Previous&&direction!=AccessibleNavigation.Up&&direction!=AccessibleNavigation.Down&&direction!=AccessibleNavigation.Left&&direction!=AccessibleNavigation.Right)return null;
                var actions=page.CalibrationKeyboardActions();int at=actions.FindIndex(a=>a.Id==id);
                int next=at+(direction==AccessibleNavigation.Next||direction==AccessibleNavigation.Down||direction==AccessibleNavigation.Right?1:-1);
                return next<0||next>=actions.Count?null:new CalibrationActionAccessibleObject(page,parent,actions[next].Id);
            }
        }
    }
}
