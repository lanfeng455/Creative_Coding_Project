using System;
using System.Text;
using UnityEngine;

namespace Focus.FlowWater
{
    // Observe the typewriter UI without referencing or editing gameplay types.
    [DefaultExecutionOrder(1000)]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(FlowWaterPreview))]
    public sealed class WaterWordEncounter : MonoBehaviour
    {
        private const string TargetWord="WATER";
        private const float PollInterval=0.1f;
        private const float StableDuration=0.2f;
        private readonly StringBuilder letters=new StringBuilder(64);
        private FlowWaterPreview water;
        private Transform alphabetBar;
        private float nextPoll;
        private float matchingSince=-1f;
        private bool triggered;

        public string CurrentWord { get; private set; }=string.Empty;
        public bool HasTriggered => triggered;

        private void Awake()
        {
            water=GetComponent<FlowWaterPreview>();
            water.WaitForWord();
        }

        private void FindAlphabetBar()
        {
            foreach(GameObject root in gameObject.scene.GetRootGameObjects())
            {
                if(root.name!="Canvas") continue;
                alphabetBar=root.transform.Find("AlphabetBar");
                if(alphabetBar!=null) return;
            }
        }

        private void LateUpdate()
        {
            if(triggered || water==null || Time.time<nextPoll) return;
            nextPoll=Time.time+PollInterval;
            if(alphabetBar==null) FindAlphabetBar();
            if(alphabetBar==null) return;

            letters.Clear();
            // Typed letters are appended as direct children of the alphabet bar.
            // Re-layout moves their positions but keeps sibling order, and erased
            // letters disappear from this scan.
            for(int index=0;index<alphabetBar.childCount;index++)
            {
                Transform child=alphabetBar.GetChild(index);
                string name=child.name;
                if(!child.gameObject.activeInHierarchy || name.Length!=7 ||
                    !name.StartsWith("Typed_",StringComparison.Ordinal)) continue;
                char letter=char.ToUpperInvariant(name[6]);
                if(letter>='A' && letter<='Z') letters.Append(letter);
            }

            int count=Mathf.Min(TargetWord.Length,letters.Length);
            CurrentWord=letters.ToString(letters.Length-count,count);
            if(!string.Equals(CurrentWord,TargetWord,StringComparison.Ordinal))
            {
                matchingSince=-1f;
                return;
            }
            if(matchingSince<0f) matchingSince=Time.time;
            if(Time.time-matchingSince<StableDuration) return;
            if(water.PlayForCompletedWord())
            {
                triggered=true;
                Debug.Log("[FlowWater] WATER completed. Starting one water sweep.",this);
            }
        }
    }
}
