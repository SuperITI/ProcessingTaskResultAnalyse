using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Diagnostics;
using System.Threading;

namespace ProcessingTaskResultAnalyse
{
    /// <summary>
    /// 目的:分析如何优雅的处理多任务返回的结果(尤其实在高并发耗时任务的状态下能够减小cpu占用和内存占用)
    /// </summary>
    class Program
    {
        static async Task Main(string[] args)
        {
            //创建耗时的Task,每个Task返回一个创建时对应的序标
            List<Task<int>> taskList = new List<Task<int>>();
            int taskCount = 1_000_000;
            for (int i = 0; i < taskCount; i++)
            {
                int tmp = i;
                taskList.Add(Task.Delay(5000).ContinueWith(t => tmp));
            }

            //测试:
            {
                List<Task<TimeSpan>> testTasks = new List<Task<TimeSpan>>();

                //Method1:
                Task<TimeSpan> method1 = Method1(taskList);
                testTasks.Add(method1);
                //Method2:
                Task<TimeSpan> method2 = Method2(taskList);
                testTasks.Add(method2);
                //Method3:
                Task<TimeSpan> method3 = Method3(taskList);
                testTasks.Add(method3);

                int methodIndex = 0;
                while (testTasks.Any())
                {
                    Task<Task<TimeSpan>> t = Task.WhenAny(testTasks);
                    Task<TimeSpan> tSrc = await t;
                    TimeSpan time = await tSrc;
                    if (tSrc == method1)
                    {
                        methodIndex = 1;
                    }
                    else if (tSrc == method2)
                    {
                        methodIndex = 2;
                    }
                    else
                    {
                        methodIndex = 3;
                    }
                    Console.WriteLine($"\"Method{methodIndex}\"耗时:{time.TotalMilliseconds}ms");
                    testTasks.Remove(tSrc);
                }
            }

            Console.WriteLine("应用程序结束!");
        }

        /// <summary>
        /// 处理方法1
        /// <para>缺点:由于使用了foreach对Task列表进行了按顺序的迭代访问,</para>
        /// 所以Task返回的结果不能根据Task自身完成的时间点进行优先的结果处理
        /// </summary>
        /// <param name="tasks"></param>
        /// <returns></returns>
        private static async Task<TimeSpan> Method1(List<Task<int>> tasks)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            foreach (var item in tasks)
            {
                int index = await item;
                //Console.WriteLine($"已经处理序标为\"{index}\"的Task返回的结果");
            }
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }

        /// <summary>
        /// 处理方法2(Task不是很多时,并不会影响性能)
        /// <para>缺点:如果Task非常多时由于使用了Task.WhenAny每个Task又增加了一次额外的开销,</para>
        /// 所以此方法会消耗更多的内存资源和CPU占用
        /// </summary>
        /// <param name="tasks"></param>
        /// <returns></returns>
        private static async Task<TimeSpan> Method2(List<Task<int>> tasks)
        {
            int counter = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (counter != tasks.Count())
            {
                Task<Task<int>> task = Task.WhenAny(tasks);
                int index = await await task;
                //Console.WriteLine($"已经处理序标为\"{index}\"的Task返回的结果");
                counter++;
            }
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }

        /// <summary>
        /// 处理方法3
        /// <para>使用<see cref="WrapTasks{T}(List{Task{T}})"/>将Task集合进行延时转换</para>
        /// </summary>
        /// <param name="tasks"></param>
        /// <returns></returns>
        private static async Task<TimeSpan> Method3(List<Task<int>> tasks)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            foreach (var item in WrapTasks(tasks))
            {
                int index = await await item;
                //Console.WriteLine($"已经处理序标为\"{index}\"的Task返回的结果");
            }
            stopwatch.Stop();
            return stopwatch.Elapsed;
        }

        /// <summary>
        /// 处理传入的Task集合,将其使用<see cref="TaskCompletionSource"/>进行封装,动态地将Task完成的结果放入<see cref="TaskCompletionSource"/>的Result中.
        /// <para>实现方式:</para>
        /// <para>第一步:为每个Task新建一个接收其结果完成的空Task(暂且称为"桶"<see cref="TaskCompletionSource"/>),</para>
        /// <para>但此时"桶"不知道自己会存放哪一个Task完成后的结果</para>
        /// <para>第二步:当每个Task完成后依次将自己对应的结果放入"桶"中</para>
        /// <para>第三步:调用者只需要依次等待"桶"中有东西(及源Task的结果)时再去处理</para>
        /// <para>通过此包装后,返回的结果中,总是会按照"先结束的Task结果放在头部,依次往后放"的方式进行排列</para>
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="tasks"></param>
        /// <returns></returns>
        private static Task<Task<T>>[] WrapTasks<T>(List<Task<T>> tasks)
        {
            //创建接受"桶"内容的数组
            Task<Task<T>>[] results = new Task<Task<T>>[tasks.Count];

            //创建空"桶"
            TaskCompletionSource<Task<T>>[] taskCompletionSources = new TaskCompletionSource<Task<T>>[tasks.Count];

            for (int i = 0; i < tasks.Count; i++)
            {
                taskCompletionSources[i] = new TaskCompletionSource<Task<T>>();
                //接受"桶"内容的数组进行赋值
                results[i] = taskCompletionSources[i].Task;
            }

            //空"桶"放入值的操作
            int index = -1;
            Action<Task<T>> continute = completed =>
            {
                TaskCompletionSource<Task<T>> taskCompletionSource = taskCompletionSources[Interlocked.Increment(ref index)];
                taskCompletionSource.SetResult(completed);
            };

            foreach (Task<T> task in tasks)
            {
                //Task结束后,将值放入空"桶"
                task.ContinueWith(continute, CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
            }

            //返回对源Task结果进行包装后的新Task
            return results;
        }
    }
}
