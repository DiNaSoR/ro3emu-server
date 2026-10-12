// Decompiles every .luac under <inDir> into <outDir> (same tree, .lua) in one JVM.
// usage: java -cp unluac.jar;. BatchUnluac <inDir> <outDir> [threads]
import java.nio.file.*;
import java.util.*;
import java.util.concurrent.*;
import java.util.concurrent.atomic.*;

public class BatchUnluac {
    public static void main(String[] args) throws Exception {
        Path in = Paths.get(args[0]), out = Paths.get(args[1]);
        int threads = args.length > 2 ? Integer.parseInt(args[2]) : 6;
        List<Path> files;
        try (var s = Files.walk(in)) { files = s.filter(p -> p.toString().endsWith(".luac")).toList(); }
        ExecutorService pool = Executors.newFixedThreadPool(threads);
        AtomicInteger ok = new AtomicInteger(), bad = new AtomicInteger();
        List<String> failures = Collections.synchronizedList(new ArrayList<>());
        for (Path f : files) {
            pool.submit(() -> {
                String rel = in.relativize(f).toString();
                Path o = out.resolve(rel.substring(0, rel.length() - 5) + ".lua");
                try {
                    Files.createDirectories(o.getParent());
                    unluac.Main.decompile(f.toString(), o.toString(), new unluac.Configuration());
                    ok.incrementAndGet();
                } catch (Throwable t) {
                    bad.incrementAndGet();
                    failures.add(rel + "\t" + t);
                }
            });
        }
        pool.shutdown();
        pool.awaitTermination(1, TimeUnit.DAYS);
        Collections.sort(failures);
        Files.write(out.resolve("_failures.tsv"), failures);
        System.out.println("ok " + ok + ", failed " + bad);
    }
}
