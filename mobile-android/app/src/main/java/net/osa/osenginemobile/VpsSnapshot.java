package net.osa.osenginemobile;

import java.util.ArrayList;
import java.util.List;

final class VpsSnapshot {
    static final class Terminal {
        /** technical name (service osengine = main, osengine-x = x): used for connections, ports and calls */
        String name;
        /** the name somebody gave the terminal in OsEngineVPS (/etc/osengine/names.conf on the VPS); null = none */
        String title;
        String service;

        /** what a person sees: the given name or, until there is one, the technical name */
        String shownName() {
            return title == null || title.trim().isEmpty() ? name : title.trim();
        }

        String state;
        long memoryBytes;
        double cpuPercent = Double.NaN;
    }

    final List<Terminal> terminals = new ArrayList<>();
    double cpuPercent = Double.NaN;
    double ramPercent = Double.NaN;
    double diskPercent = Double.NaN;
    long ramTotal;
    long diskTotal;
    int cores;
    long ramUsed;
}
