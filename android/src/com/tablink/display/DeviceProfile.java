package com.tablink.display;

import android.media.*;
import android.os.Build;
import android.view.Display;
import java.io.IOException;

/** Negotiate with actual decoder capabilities, independently of device brand. */
final class DeviceProfile {
    static final int[][] SIZES = {
        {3840,2160},{3200,2000},{3120,2080},{2960,1848},{2880,1800},{2800,1752},
        {2736,1824},{2560,1600},{2560,1440},{2400,1600},{2400,1080},{2304,1440},
        {2240,1400},{2160,1440},{2160,1350},{2000,1200},{1920,1200},{1920,1080},
        {1600,1000},{1600,900},{1440,900},{1280,800},{1280,720},{1024,768},
        {1024,600},{960,540},{800,480},{640,360}
    };
    final int width, height, rate, bits;
    DeviceProfile(int w,int h,int r,int b){width=w;height=h;rate=r;bits=b;}
    static String decoder(int w,int h,int rate,int bits) {
        int profile=bits==10?MediaCodecInfo.CodecProfileLevel.HEVCProfileMain10:MediaCodecInfo.CodecProfileLevel.HEVCProfileMain;
        for(MediaCodecInfo info:new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
            if(info.isEncoder())continue;
            String name=info.getName().toLowerCase(java.util.Locale.ROOT);
            if(Build.VERSION.SDK_INT>=29 ? !info.isHardwareAccelerated() : (name.startsWith("omx.google.") || name.startsWith("c2.android.")))continue;
            try {
                MediaCodecInfo.CodecCapabilities caps=info.getCapabilitiesForType("video/hevc");
                boolean supports=false;
                for(MediaCodecInfo.CodecProfileLevel level:caps.profileLevels)if(level.profile==profile)supports=true;
                if(supports && caps.getVideoCapabilities().areSizeAndRateSupported(w,h,rate))return info.getName();
            }catch(IllegalArgumentException ignored){}
        }
        return null;
    }
    static DeviceProfile detect(Display display) throws IOException {
        Display.Mode nativeMode=display.getMode();
        int maxW=Math.max(nativeMode.getPhysicalWidth(),nativeMode.getPhysicalHeight());
        int maxH=Math.min(nativeMode.getPhysicalWidth(),nativeMode.getPhysicalHeight());
        float maxHz=display.getRefreshRate();
        for(Display.Mode mode:display.getSupportedModes()) {
            if(Math.max(mode.getPhysicalWidth(),mode.getPhysicalHeight())==maxW && Math.min(mode.getPhysicalWidth(),mode.getPhysicalHeight())==maxH)
                maxHz=Math.max(maxHz,mode.getRefreshRate());
        }
        DeviceProfile best=null; double bestScore=Double.NEGATIVE_INFINITY;
        for(int[] size:SIZES) {
            int w=size[0],h=size[1];
            if(w>maxW || h>maxH)continue;
            for(int rate:new int[]{120,90,75,60,30}) {
                if(rate>maxHz+0.6f)continue;
                for(int bits:new int[]{10,8}) {
                    if(decoder(w,h,rate,bits)==null)continue;
                    double aspect=Math.abs((double)w/h-(double)maxW/maxH);
                    double score=(double)w*h/(maxW*maxH)*1000-aspect*2000+rate*0.5+(bits==10?5:0);
                    if(score>bestScore){bestScore=score;best=new DeviceProfile(w,h,rate,bits);}
                    break;
                }
            }
        }
        if(best==null)throw new IOException("Este dispositivo necesita un decodificador HEVC de hardware compatible.");
        return best;
    }
}
