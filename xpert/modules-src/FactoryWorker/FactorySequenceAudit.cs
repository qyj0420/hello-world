using System;
using System.IO;
using System.Xml.Serialization;

namespace Xiongxin.FactoryTest
{
    public sealed class FactorySequenceAudit
    {
        public DateTime Utc;
        public string ArtSerial, PicoSerial, SequenceId, Event, Detail;
        public int Attempt, Round, Completed, Remaining, Requested, CapturedRangeIndex;
        public bool Simulation;
        internal static void Save(FactoryResult r,string code,string text,int completed,int count)
        {
            var record = new FactorySequenceAudit { Utc=DateTime.UtcNow, ArtSerial=r.Capture.ArtSerial,
                PicoSerial=r.Capture.PicoSerial, SequenceId=r.Settings.SequenceId, Attempt=r.Settings.SequenceAttempt,
                Round=r.Settings.SequenceRound, Completed=completed, Remaining=count-completed, Requested=count,
                CapturedRangeIndex=r.Capture.Settings.RangeIndex, Simulation=r.Settings.Simulation, Event=code, Detail=text };
            using(var f=new FileStream(Path.Combine(r.DirectoryPath,"sequence-"+Guid.NewGuid().ToString("N")+".xml"),FileMode.CreateNew))
                new XmlSerializer(typeof(FactorySequenceAudit)).Serialize(f,record);
        }
    }
}
