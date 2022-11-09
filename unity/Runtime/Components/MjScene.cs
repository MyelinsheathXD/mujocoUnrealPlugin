// Copyright 2019 DeepMind Technologies Limited
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using UnityEngine;
using UnityEngine.Profiling;

namespace Mujoco {

public class PhysicsRuntimeException : Exception {
  public PhysicsRuntimeException(string message) : base(message) {}
}

public class MjScene : MonoBehaviour {

  public unsafe MujocoLib.mjModel_* Model = null;
  public unsafe MujocoLib.mjData_* Data = null;

        //public unsafe Tuple< MujocoLib.mjModel_*, MujocoLib.mjData_*> extraModelandData;
        public unsafe MujocoLib.mjModel_*[] extModels;
        public unsafe MujocoLib.mjData_*[] extData;
        int DefaultListID = 0;

        public List <int> layersList ;
  // Public and global access to the active MjSceneGenerationContext.
  // Throws an exception if accessed when the scene is not being generated.
  public MjcfGenerationContext GenerationContext {
    get {
      if (_generationContext == null) {
        throw new InvalidOperationException(
            "This property can only be accessed from the scope of MjComponent.GenerateMjcf().");
      }
      return _generationContext;
    }
  }

  public static MjScene Instance {
    get {
      if (_instance == null) {
        var instances = FindObjectsOfType<MjScene>();
        if (instances.Length >= 1) { // even one is too much - _instance shouldn't have been null.
          throw new InvalidOperationException(
              "A MjScene singleton is created automatically, yet multiple instances exist.");
        } else {
          GameObject go = new GameObject("MjScene");
          _instance = go.AddComponent<MjScene>();
        }
      }
      return _instance;
    }
  }

  public void Awake() {
    if (_instance == null) {
      _instance = this;
    } else if (_instance != this) {
      throw new InvalidOperationException(
          "MjScene is a singleton, yet multiple instances found.");
    }
  }

  private static MjScene _instance = null;

  private List<MjComponent> _orderedComponents;
  private List<List<MjComponent>> L_orderedComponents;
  protected unsafe void Start() {
    CreateScene();
  }

  protected unsafe void OnDestroy() {
    DestroyScene();
  }

  protected unsafe void FixedUpdate() {
    StepScene();
  }

  public bool SceneRecreationAtLateUpdateRequested = false;

  protected unsafe void LateUpdate() {
    if (SceneRecreationAtLateUpdateRequested) {
      RecreateScene();
      SceneRecreationAtLateUpdateRequested = false;
    }
  }

  // Context used during scene generation where components will store their shared dependencies
  // (such as the mesh assets they reference, or common compiler settings that need to be resolved
  // globally).
  // The field will be initialized with a class instance only during the scene generation.
  // A public read-only property will provide MjComponents with access to the instance
  // of that field.
  private MjcfGenerationContext _generationContext;

  // Builds a new Mujoco scene.
  public unsafe XmlDocument CreateScene(bool skipCompile=false) {
    if (_generationContext != null) {
      throw new InvalidOperationException(
          "The scene is currently being generated on another thread.");
    }
    // Linearize the hierarchy of transforms of the components.
    // We will use this list to update the transforms of the associated GameObject in a way that
    // ensures we first update the parent transforms, and then we progress down the hierarchy
    // tree and update the children.
    // We're doing this, because we will use Unity's hierarchical transforms system to translate
    // Mujoco elements' world transforms directly to Unity's world coordinates. That however
    // will only work if we can guarantee that for every child, the parent transforms have already
    // been recalculated.
    //
    // DESIGN: An alternative to that would be to operate in local space on the Unity side.
    // I briefly explored that approach, but decided against it. It increases the amount of code
    // on the side of the individual components. This solution allows to restrict the code in the
    // components to a bare minimum, at the expense of one extra method here.

    //filters
    List <List<MjComponent>> listLmjcom=new List<List<MjComponent>>();
            var hierarchyRootsAll = FindObjectsOfType<MjComponent>();
            layersList = new List<int>();
            for (int i = 0; i < hierarchyRootsAll.Length; i++)
            {
                int LayerCurrent = hierarchyRootsAll[i].gameObject.layer;
                bool exists = false;
                for (int k = 0; k < layersList.Count; k++)
                {
                    if (LayerCurrent == layersList[k])
                    {                      
                        listLmjcom[k].Add(hierarchyRootsAll[i]);
                        exists = true;
                        break;
                    }

                }
                if (exists == false)
                {
                    layersList.Add(LayerCurrent);
                    List<MjComponent> compList = new List<MjComponent>();
                    compList.Add(hierarchyRootsAll[i]);
                    listLmjcom.Add(compList);
                }
            }

            if (layersList.Count>1)
            {
                for (int i = 0; i < layersList.Count; i++)
                {
                    if (layersList[i]==0)
                    {
                        DefaultListID = i;
                    }
                }

                extData = new MujocoLib.mjData_*[layersList.Count];
                extModels = new MujocoLib.mjModel_*[layersList.Count];

                L_orderedComponents = new List<List<MjComponent>>();
            }
            //



            var hierarchyRoots = listLmjcom[DefaultListID].ToArray()
        .Where(component => MjHierarchyTool.FindParentComponent(component) == null)
        .Select(component => component.transform)
        .Distinct();
    _orderedComponents = new List<MjComponent>();
    foreach (var root in hierarchyRoots) {
      _orderedComponents.AddRange(MjHierarchyTool.LinearizeHierarchyBFS(root));
    }

            {// list orderedComponents
                for (int i = 0; i < layersList.Count; i++)
                {

                    if (i==DefaultListID)
                    {
                        L_orderedComponents.Add( new List<MjComponent>());
                        continue;
                    }

                    var hierarchyRootsL = listLmjcom[layersList[i]].ToArray()
                        .Where(component => MjHierarchyTool.FindParentComponent(component) == null)
                        .Select(component => component.transform)
                        .Distinct();
                    //List < MjComponent> orComp = new List<MjComponent>();
                    L_orderedComponents.Add(new List<MjComponent>());
                    //L_orderedComponents[i] = new List<MjComponent>();
                    foreach (var root in hierarchyRootsL)
                    {
                        L_orderedComponents[i].AddRange(MjHierarchyTool.LinearizeHierarchyBFS(root));
                    }


                }




            }

    XmlDocument sceneMjcf = null;    
    List<XmlDocument>LsceneMJcf=new List<XmlDocument> ();
    try {
      _generationContext = new MjcfGenerationContext();
      sceneMjcf = GenerateSceneMjcf(_orderedComponents);
    } catch (Exception e) {
      _generationContext = null;
      Debug.LogException(e);
#if UNITY_EDITOR
      UnityEditor.EditorApplication.isPlaying = false;
#else
      Application.Quit();
#endif
      throw;
    }
    _generationContext = null;



            //{///generate file 
                for (int i = 0; i < layersList.Count; i++)
                {
                    if (layersList[i]==DefaultListID)
                    {

                        
                        LsceneMJcf.Add(sceneMjcf);
                        continue;
                        
                    }


                    try
                    {
                        _generationContext = new MjcfGenerationContext();
                        XmlDocument sceneMjcf1 = GenerateSceneMjcf(L_orderedComponents[i]);
                        LsceneMJcf.Add(sceneMjcf1);
                    }
                    catch (Exception e)
                    {
                        _generationContext = null;
                        Debug.LogException(e);
#if UNITY_EDITOR
                        UnityEditor.EditorApplication.isPlaying = false;
#else
      Application.Quit();
#endif
                        throw;
                    }
                    _generationContext = null;

                }




            //}

            // Save the Mjcf to a file for debug purposes.
            var settings = MjGlobalSettings.Instance;
    if (settings && !string.IsNullOrEmpty(settings.DebugFileName)) {
      SaveToFile(sceneMjcf, Path.Combine(Application.temporaryCachePath, settings.DebugFileName));
    }


    if (!skipCompile) {
      // Compile the scene from the Mjcf.
      CompileScene(sceneMjcf, _orderedComponents,LsceneMJcf,L_orderedComponents);

    }

            //print(sceneMjcf.OuterXml);

    return sceneMjcf;
  }

  private unsafe void CompileScene(
      XmlDocument mjcf, IEnumerable<MjComponent> components, List<XmlDocument>Lmjcf,List<List<MjComponent>>Lcomponents) {
    Model = MjEngineTool.LoadModelFromString(mjcf.OuterXml);
    if (Model == null) {
      throw new NullReferenceException("Failed to create Mujoco runtime model.");
    } else {
      Data = MujocoLib.mj_makeData(Model);
    }
    if (Data == null) {
      throw new NullReferenceException("Failed to create Mujoco runtime data.");
    }

    // Bind the components to their Mujoco counterparts.
    foreach (var component in components) {
      component.BindToRuntime(Model, Data);
    }

            for (int i = 0; i < layersList.Count; i++)
            {
                if (layersList[i]== DefaultListID)
                {
                    extModels[i] = Model;
                    extData[i] = Data;
                    continue;
                }


                extModels[i] = MjEngineTool.LoadModelFromString(Lmjcf[i].OuterXml);
                if (extModels[i] == null)
                {
                    throw new NullReferenceException("Failed to create Mujoco runtime model.");
                }
                else
                {
                    extData[i] = MujocoLib.mj_makeData(extModels[i]);
                }
                if (extData[i] == null)
                {
                    throw new NullReferenceException("Failed to create Mujoco runtime data.");
                }

                // Bind the components to their Mujoco counterparts.
                foreach (var component in Lcomponents[i])
                {
                    component.BindToRuntime(extModels[i], extData[i]);
                }

            }

  }

  public unsafe void SyncUnityToMjState() {
    foreach (var component in _orderedComponents) {
      if (component != null && component.isActiveAndEnabled) {
        component.OnSyncState(Data);
      }
    }
            for (int i = 0; i < layersList.Count; i++)
            {
                if (i==DefaultListID)
                {
                    continue;
                }
                foreach (var component in L_orderedComponents[i])
                {
                    if (component != null && component.isActiveAndEnabled)
                    {
                        component.OnSyncState(extData[i]);
                    }
                }

            }
  }

  // This must be called after every change in the Unity scene during runtime, in order to keep the
  // MuJoCo physics scene in sync.  The spatial arrangement in the MJCF that creates the new physics
  // is defined by the Unity transforms; therefore, we:
  // 1. cache the joint states;
  // 2. reset the Unity scene to its configuration at initialization;
  // 3. recreate the scene in this pristine configuration;
  // 4. rehydrate the physics scene, and sync the Unity scene to it.
  public unsafe void RecreateScene() {
    // cache joint states in order to re-apply it to the new scene
    var joints = FindObjectsOfType<MjBaseJoint>();
    var positions = new Dictionary<MjBaseJoint, double[]>();
    var velocities = new Dictionary<MjBaseJoint, double[]>();
    foreach (var joint in joints) {
      if (joint.QposAddress > -1) { // newly added components shouldn't be cached
        switch (Model->jnt_type[joint.MujocoId]) {
          default:
          case (int)MujocoLib.mjtJoint.mjJNT_HINGE:
          case (int)MujocoLib.mjtJoint.mjJNT_SLIDE:
            positions[joint] = new double[] {Data->qpos[joint.QposAddress]};
            velocities[joint] = new double[] {Data->qvel[joint.DofAddress]};
            break;
          case (int)MujocoLib.mjtJoint.mjJNT_BALL:
            positions[joint] = new double[] {
                Data->qpos[joint.QposAddress],
                Data->qpos[joint.QposAddress+1],
                Data->qpos[joint.QposAddress+2],
                Data->qpos[joint.QposAddress+3]};
            velocities[joint] = new double[] {
                Data->qvel[joint.DofAddress],
                Data->qvel[joint.DofAddress+1],
                Data->qvel[joint.DofAddress+2]};
            break;
          case (int)MujocoLib.mjtJoint.mjJNT_FREE:
            positions[joint] = new double[] {
                Data->qpos[joint.QposAddress],
                Data->qpos[joint.QposAddress+1],
                Data->qpos[joint.QposAddress+2],
                Data->qpos[joint.QposAddress+3],
                Data->qpos[joint.QposAddress+4],
                Data->qpos[joint.QposAddress+5],
                Data->qpos[joint.QposAddress+6]};
            velocities[joint] = new double[] {
                Data->qvel[joint.DofAddress],
                Data->qvel[joint.DofAddress+1],
                Data->qvel[joint.DofAddress+2],
                Data->qvel[joint.DofAddress+3],
                Data->qvel[joint.DofAddress+4],
                Data->qvel[joint.DofAddress+5]};
            break;
        }
      }
    }

    // update unity transforms according to qpos0, so they're ready to create the new MJCF
    MujocoLib.mj_resetData(Model, Data);
    MujocoLib.mj_kinematics(Model, Data);
    SyncUnityToMjState();

            // Delete previous model, data
            //DestroyScene();


            //specific destroy
            if (Model != null)
            {
                MujocoLib.mj_deleteModel(Model);
                Model = null;
            }
            if (Data != null)
            {
                MujocoLib.mj_deleteData(Data);
                Data = null;
            }

            // Create a new MJCF and new model+data, indices may be different
            CreateScene();

    // for joints that persisted, set state of the new MuJoCo scene according to the cached state
    foreach (var joint in joints) {
      try {
        var position = positions[joint]; // this will fail for new joints, hence try/catch
        var velocity = velocities[joint];
        switch (Model->jnt_type[joint.MujocoId]) {
          default:
          case (int)MujocoLib.mjtJoint.mjJNT_HINGE:
          case (int)MujocoLib.mjtJoint.mjJNT_SLIDE:
            Data->qpos[joint.QposAddress] = position[0];
            Data->qvel[joint.DofAddress] = velocity[0];
            break;
          case (int)MujocoLib.mjtJoint.mjJNT_BALL:
            Data->qpos[joint.QposAddress] = position[0];
            Data->qpos[joint.QposAddress+1] = position[1];
            Data->qpos[joint.QposAddress+2] = position[2];
            Data->qpos[joint.QposAddress+3] = position[3];
            Data->qvel[joint.DofAddress] = velocity[0];
            Data->qvel[joint.DofAddress+1] = velocity[1];
            Data->qvel[joint.DofAddress+2] = velocity[2];
            break;
          case (int)MujocoLib.mjtJoint.mjJNT_FREE:
            Data->qpos[joint.QposAddress] = position[0];
            Data->qpos[joint.QposAddress+1] = position[1];
            Data->qpos[joint.QposAddress+2] = position[2];
            Data->qpos[joint.QposAddress+3] = position[3];
            Data->qpos[joint.QposAddress+4] = position[4];
            Data->qpos[joint.QposAddress+5] = position[5];
            Data->qpos[joint.QposAddress+6] = position[6];
            Data->qvel[joint.DofAddress] = velocity[0];
            Data->qvel[joint.DofAddress+1] = velocity[1];
            Data->qvel[joint.DofAddress+2] = velocity[2];
            Data->qvel[joint.DofAddress+3] = velocity[3];
            Data->qvel[joint.DofAddress+4] = velocity[4];
            Data->qvel[joint.DofAddress+5] = velocity[5];
            break;
        }
      } catch {}
    }
    // update mj transforms:
    MujocoLib.mj_kinematics(Model, Data);


            //
            for (int i = 0; i < layersList.Count; i++)
            {
                if (DefaultListID == layersList[i])
                {
                    continue;
                }

                var joints1 = FindObjectsOfType<MjBaseJoint>();
                var positions1 = new Dictionary<MjBaseJoint, double[]>();
                var velocities1 = new Dictionary<MjBaseJoint, double[]>();
                foreach (var joint in joints1)
                {
                    if (joint.QposAddress > -1)
                    { // newly added components shouldn't be cached
                        switch (extModels[i]->jnt_type[joint.MujocoId])
                        {
                            default:
                            case (int)MujocoLib.mjtJoint.mjJNT_HINGE:
                            case (int)MujocoLib.mjtJoint.mjJNT_SLIDE:
                                positions1[joint] = new double[] { extData[i]->qpos[joint.QposAddress] };
                                velocities1[joint] = new double[] { extData[i]->qvel[joint.DofAddress] };
                                break;
                            case (int)MujocoLib.mjtJoint.mjJNT_BALL:
                                positions1[joint] = new double[] {
                extData[i]->qpos[joint.QposAddress],
                extData[i]->qpos[joint.QposAddress+1],
                extData[i]->qpos[joint.QposAddress+2],
                extData[i]->qpos[joint.QposAddress+3]};
                                velocities1[joint] = new double[] {
                extData[i]->qvel[joint.DofAddress],
                extData[i]->qvel[joint.DofAddress+1],
                extData[i]->qvel[joint.DofAddress+2]};
                                break;
                            case (int)MujocoLib.mjtJoint.mjJNT_FREE:
                                positions1[joint] = new double[] {
                extData[i]->qpos[joint.QposAddress],
                extData[i]->qpos[joint.QposAddress+1],
                extData[i]->qpos[joint.QposAddress+2],
                extData[i]->qpos[joint.QposAddress+3],
                extData[i]->qpos[joint.QposAddress+4],
                extData[i]->qpos[joint.QposAddress+5],
                extData[i]->qpos[joint.QposAddress+6]};
                                velocities1[joint] = new double[] {
                extData[i]->qvel[joint.DofAddress],
                extData[i]->qvel[joint.DofAddress+1],
                extData[i]->qvel[joint.DofAddress+2],
                extData[i]->qvel[joint.DofAddress+3],
                extData[i]->qvel[joint.DofAddress+4],
                extData[i]->qvel[joint.DofAddress+5]};
                                break;
                        }
                    }
                }

                // update unity transforms according to qpos0, so they're ready to create the new MJCF
                MujocoLib.mj_resetData(extModels[i], extData[i]);
                MujocoLib.mj_kinematics(extModels[i], extData[i]);
                SyncUnityToMjState();

                // Delete previous model, data
                //DestroyScene();

                if (extModels[i] != null)
                {
                    MujocoLib.mj_deleteModel(extModels[i]);
                    extModels[i] = null;
                }
                if (extData[i] != null)
                {
                    MujocoLib.mj_deleteData(extData[i]);
                    extData[i] = null;
                }
                // Create a new MJCF and new model+data, indices may be different
                CreateScene();

                // for joints that persisted, set state of the new MuJoCo scene according to the cached state
                foreach (var joint in joints1)
                {
                    try
                    {
                        var position = positions1[joint]; // this will fail for new joints, hence try/catch
                        var velocity = velocities1[joint];
                        switch (extModels[i]->jnt_type[joint.MujocoId])
                        {
                            default:
                            case (int)MujocoLib.mjtJoint.mjJNT_HINGE:
                            case (int)MujocoLib.mjtJoint.mjJNT_SLIDE:
                                extData[i]->qpos[joint.QposAddress] = position[0];
                                extData[i]->qvel[joint.DofAddress] = velocity[0];
                                break;
                            case (int)MujocoLib.mjtJoint.mjJNT_BALL:
                                extData[i]->qpos[joint.QposAddress] = position[0];
                                extData[i]->qpos[joint.QposAddress + 1] = position[1];
                                extData[i]->qpos[joint.QposAddress + 2] = position[2];
                                extData[i]->qpos[joint.QposAddress + 3] = position[3];
                                extData[i]->qvel[joint.DofAddress] = velocity[0];
                                extData[i]->qvel[joint.DofAddress + 1] = velocity[1];
                                extData[i]->qvel[joint.DofAddress + 2] = velocity[2];
                                break;
                            case (int)MujocoLib.mjtJoint.mjJNT_FREE:
                                extData[i]->qpos[joint.QposAddress] = position[0];
                                extData[i]->qpos[joint.QposAddress + 1] = position[1];
                                extData[i]->qpos[joint.QposAddress + 2] = position[2];
                                extData[i]->qpos[joint.QposAddress + 3] = position[3];
                                extData[i]->qpos[joint.QposAddress + 4] = position[4];
                                extData[i]->qpos[joint.QposAddress + 5] = position[5];
                                extData[i]->qpos[joint.QposAddress + 6] = position[6];
                                extData[i]->qvel[joint.DofAddress] = velocity[0];
                                extData[i]->qvel[joint.DofAddress + 1] = velocity[1];
                                extData[i]->qvel[joint.DofAddress + 2] = velocity[2];
                                extData[i]->qvel[joint.DofAddress + 3] = velocity[3];
                                extData[i]->qvel[joint.DofAddress + 4] = velocity[4];
                                extData[i]->qvel[joint.DofAddress + 5] = velocity[5];
                                break;
                        }
                    }
                    catch { }
                }
                // update mj transforms:
                MujocoLib.mj_kinematics(extModels[i], extData[i]);


            }
            //
    SyncUnityToMjState();
  }

  // Destroys the Mujoco scene.
  public unsafe void DestroyScene() {
    if (Model != null) {
      MujocoLib.mj_deleteModel(Model);
      Model = null;
    }
    if (Data != null) {
      MujocoLib.mj_deleteData(Data);
      Data = null;
    }

            for (int i = 0; i < layersList.Count; i++)
            {
                if (DefaultListID == layersList[i])
                {
                    continue;
                }
                if (extModels[i] != null)
                {
                    MujocoLib.mj_deleteModel(extModels[i]);
                    extModels[i] = null;
                }
                if (extData[i] != null)
                {
                    MujocoLib.mj_deleteData(extData[i]);
                    extData[i] = null;
                }

            }
  }

  // Updates the scene and the state of Mujoco simulation.
  public unsafe void StepScene() {
    if (Model == null || Data == null) {
      throw new NullReferenceException("Failed to create Mujoco runtime.");
    }
    Profiler.BeginSample("MjStep");
    Profiler.BeginSample("MjStep.mj_step");
    MujocoLib.mj_step(Model, Data);

            //{
                for (int i = 0; i < layersList.Count; i++)
                {
                    if (i==DefaultListID)
                    {
                        continue;
                    }

                    MujocoLib.mj_step(extModels[i], extData[i]);
                }

            //}
    Profiler.EndSample(); // MjStep.mj_step
    CheckForPhysicsException();

    Profiler.BeginSample("MjStep.OnSyncState");
    SyncUnityToMjState();
    Profiler.EndSample(); // MjStep.OnSyncState
    Profiler.EndSample(); // MjStep
  }

  private unsafe void CheckForPhysicsException() {
    if (Data->warning0.number > 0) {
      Data->warning0.number = 0;
      throw new PhysicsRuntimeException("INERTIA: (Near-) Singular inertia matrix.");
    }
    if (Data->warning1.number > 0) {
      Data->warning1.number = 0;
      throw new PhysicsRuntimeException($"CONTACTFULL: nconmax {Model->nconmax} isn't sufficient.");
    }
    if (Data->warning2.number > 0) {
      Data->warning2.number = 0;
      throw new PhysicsRuntimeException("CNSTRFULL: njmax {Model.njmax} isn't sufficient.");
    }
    if (Data->warning3.number > 0) {
      Data->warning3.number = 0;
      throw new PhysicsRuntimeException("VGEOMFULL: who constructed a mjvScene?!");
    }
    if (Data->warning4.number > 0) {
      Data->warning4.number = 0;
      throw new PhysicsRuntimeException("BADQPOS: NaN/inf in qpos.");
    }
    if (Data->warning5.number > 0) {
      Data->warning5.number = 0;
      throw new PhysicsRuntimeException("BADQVEL: NaN/inf in qvel.");
    }
    if (Data->warning6.number > 0) {
      Data->warning6.number = 0;
      throw new PhysicsRuntimeException("BADQACC: NaN/inf in qacc.");
    }
    if (Data->warning7.number > 0) {
      Data->warning7.number = 0;
      throw new PhysicsRuntimeException("BADCTRL: NaN/inf in ctrl.");
    }

            for (int i = 0; i < layersList.Count; i++)
            {
                if (DefaultListID == layersList[i])
                {
                    continue;
                }
                if (extData[i]->warning0.number > 0)
                {
                    extData[i]->warning0.number = 0;
                    throw new PhysicsRuntimeException("INERTIA: (Near-) Singular inertia matrix.");
                }
                if (extData[i]->warning1.number > 0)
                {
                    extData[i]->warning1.number = 0;
                    throw new PhysicsRuntimeException($"CONTACTFULL: nconmax {Model->nconmax} isn't sufficient.");
                }
                if (extData[i]->warning2.number > 0)
                {
                    extData[i]->warning2.number = 0;
                    throw new PhysicsRuntimeException("CNSTRFULL: njmax {Model.njmax} isn't sufficient.");
                }
                if (extData[i]->warning3.number > 0)
                {
                    extData[i]->warning3.number = 0;
                    throw new PhysicsRuntimeException("VGEOMFULL: who constructed a mjvScene?!");
                }
                if (extData[i]->warning4.number > 0)
                {
                    extData[i]->warning4.number = 0;
                    throw new PhysicsRuntimeException("BADQPOS: NaN/inf in qpos.");
                }
                if (extData[i]->warning5.number > 0)
                {
                    extData[i]->warning5.number = 0;
                    throw new PhysicsRuntimeException("BADQVEL: NaN/inf in qvel.");
                }
                if (extData[i]->warning6.number > 0)
                {
                    extData[i]->warning6.number = 0;
                    throw new PhysicsRuntimeException("BADQACC: NaN/inf in qacc.");
                }
                if (extData[i]->warning7.number > 0)
                {
                    extData[i]->warning7.number = 0;
                    throw new PhysicsRuntimeException("BADCTRL: NaN/inf in ctrl.");
                }

            }
  }

  // Generate a Mujoco scene description using the specified components.
  private XmlDocument GenerateSceneMjcf(IEnumerable<MjComponent> components) {
    var doc = new XmlDocument();
    var MjRoot = (XmlElement)doc.AppendChild(doc.CreateElement("mujoco"));

    // Scene definition section.
    var worldMjcf = (XmlElement)MjRoot.AppendChild(doc.CreateElement("worldbody"));
    BuildHierarchicalMjcf(
        doc,
        components.Where(component =>
            (component is MjBaseBody) ||
            (component is MjInertial) ||
            (component is MjBaseJoint) ||
            (component is MjGeom) ||
            (component is MjSite)),
        worldMjcf);

    // Non-hierarchical sections:
    MjRoot.AppendChild(GenerateMjcfSection(
        doc, components.Where(component => component is MjExclude), "contact"));

    MjRoot.AppendChild(GenerateMjcfSection(
        doc, components.Where(component => component is MjBaseTendon), "tendon"));

    MjRoot.AppendChild(GenerateMjcfSection(
        doc, components.Where(component => component is MjBaseConstraint), "equality"));

    MjRoot.AppendChild(GenerateMjcfSection(
        doc, components.Where(component => component is MjActuator), "actuator"));

    MjRoot.AppendChild(GenerateMjcfSection(
        doc, components.Where(component => component is MjBaseSensor), "sensor"));

    // Generate the Mjcf of the runtime dependencies added to the context.
    _generationContext.GenerateMjcf(MjRoot);
    return doc;
  }

  private XmlElement GenerateMjcfSection(
      XmlDocument doc, IEnumerable<MjComponent> components, string sectionName) {
    var section = doc.CreateElement(sectionName);
    foreach (var component in components) {
      var componentMjcf = component.GenerateMjcf(_generationContext.GenerateName(component), doc);
      section.AppendChild(componentMjcf);
    }
    return section;
  }

  private void BuildHierarchicalMjcf(
      XmlDocument doc, IEnumerable<MjComponent> components, XmlElement worldMjcf) {
    var associations = new Dictionary<MjComponent, XmlElement>();

    // Build individual Mjcfs.
    foreach (var component in components) {
      var componentMjcf = component.GenerateMjcf(
          _generationContext.GenerateName(component), doc);
      // We'll use a dictionary to define associations between the components and the corresponding
      // Mjcf elements.
      associations.Add(component, componentMjcf);
    }

    // Connect the Mjcfs into hierarchy.
    foreach (var component in components) {
      var componentMjcf = associations[component];
      var parentComponent = MjHierarchyTool.FindParentComponent(component);
      if (parentComponent != null) {
        var parentComponentMjcf = associations[parentComponent];
        parentComponentMjcf.AppendChild(componentMjcf);
      } else {
        worldMjcf.AppendChild(componentMjcf);
      }
    }
  }

  // Saves an XML document to the specified file.
  private void SaveToFile(XmlDocument document, string filePath) {
    try {
      using (var stream = File.Open(filePath, FileMode.Create)) {
        using (var writer = new XmlTextWriter(stream, new UTF8Encoding(false))) {
          writer.Formatting = Formatting.Indented;
          document.WriteContentTo(writer);
          Debug.Log($"MJCF saved to {filePath}");
        }
      }
    } catch (IOException ex) {
      Debug.LogWarning("Failed to save Xml to a file: " + ex.ToString(), this);
    }
  }
}
}
