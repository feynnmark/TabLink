using System;
namespace TabLink {
    static class ProfileTests {
        static int checks;
        static void Check(bool condition,string name){if(!condition)throw new Exception(name);checks++;}
        static void Main(){
            string key=new string('a',64);
            Check(StreamProfile.Parse(null,key)==null,"missing greeting");
            Check(StreamProfile.Parse("TABLINK/5 "+new string('b',64)+" 1920 1080 60 8",key)==null,"wrong key");
            Check(StreamProfile.Parse("TABLINK/4 "+key,key)==null,"old protocol");
            Check(StreamProfile.Parse("TABLINK/5 "+key+" 999999 1080 60 8",key)==null,"oversize");
            Check(StreamProfile.Parse("TABLINK/5 "+key+" 1921 1080 60 8",key)==null,"odd dimensions");
            Check(StreamProfile.Parse("TABLINK/5 "+key+" 1920 1080 144 8",key)==null,"unsupported rate");
            Check(StreamProfile.Parse("TABLINK/5 "+key+" 1920 1080 60 12",key)==null,"unsupported depth");
            Check(StreamProfile.Parse("TABLINK/5 "+key+" 1920 1080 60 8 extra",key)==null,"trailing data");
            foreach(int rate in new[]{30,60,75,90,120})foreach(int bits in new[]{8,10}){
                StreamProfile p=StreamProfile.Parse("TABLINK/5 "+key+" 1920 1080 "+rate+" "+bits,key);
                Check(p!=null && p.Rate==rate && p.Bits==bits,"valid negotiated profile");
            }
            StreamProfile native=StreamProfile.Parse("TABLINK/5 "+key+" 2560 1600 120 10",key);
            Check(native.Score(2560,1600,120)==0,"native mode wins");
            Check(native.Score(1920,1200,90)<native.Score(1920,1080,90),"prefer aspect ratio");
            Check(double.IsPositiveInfinity(native.Score(3840,2160,120)),"no upscaling beyond device");
            Check(double.IsPositiveInfinity(native.Score(2560,1600,144)),"no refresh beyond device");
            Check(native.Score(2560,1600,90)<native.Score(1920,1200,120),"preserve native resolution");
            Console.WriteLine("PASS "+checks+" profile and mode checks");
        }
    }
}
