using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.UnitTests.Harness;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class CalendarCoverageTests
{
    private static DateTimeOffset D(int y,int m,int d,int h=10) => new(y,m,d,h,0,0,TimeSpan.Zero);

    private static CalendarEntity SeedCal(PimDbContext db, Guid? uid=null, string kind="calendar")
    {
        var c=new CalendarEntity{UserId=uid??ServiceTestBase.DefaultUserId, Name=$"cal-{Guid.NewGuid():N}", Kind=kind, IsDefault=true, Color="#3B82F6"};
        db.Set<CalendarEntity>().Add(c); return c;
    }

    private static void AddOutlookBinding(PimDbContext db, Guid calId)
    {
        var conn=new OutlookConnectionEntity{UserId=ServiceTestBase.DefaultUserId, Provider="outlook", Status="connected"};
        db.Set<OutlookConnectionEntity>().Add(conn);
        db.SaveChanges();
        var binding=new OutlookCalendarBindingEntity{ConnectionId=conn.Id, PimCalendarId=calId, GraphCalendarId="graph-1", Name="bind"};
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        db.SaveChanges();
    }

    // ---- Calendars ----
    [Fact] public async Task Cal_Create_DefaultKindAndColor()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var r=await svc.CreateCalendarAsync(new CreateCalendarRequest("MyCal", null, null), CancellationToken.None);
        Assert.Equal("calendar", r.Kind); Assert.Equal("#3B82F6", r.Color); Assert.True(r.IsDefault);
    }
    [Fact] public async Task Cal_Create_SecondSameKind_NotDefault()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await svc.CreateCalendarAsync(new CreateCalendarRequest("A", null, "calendar"), CancellationToken.None);
        var r2=await svc.CreateCalendarAsync(new CreateCalendarRequest("B", null, "calendar"), CancellationToken.None);
        Assert.False(r2.IsDefault);
    }
    [Fact] public async Task Cal_GetCalendars_FilterKind()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await svc.CreateCalendarAsync(new CreateCalendarRequest("A", null, "calendar"), CancellationToken.None);
        await svc.CreateCalendarAsync(new CreateCalendarRequest("T", null, "task"), CancellationToken.None);
        var all=await svc.GetCalendarsAsync(null, CancellationToken.None);
        var filtered=await svc.GetCalendarsAsync("task", CancellationToken.None);
        Assert.Equal(2, all.Count); Assert.Single(filtered); Assert.Equal("task", filtered[0].Kind);
    }
    [Fact] public async Task Cal_GetCalendars_WithBinding_CanEditFlag()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        AddOutlookBinding(db, cal.Id);
        var list=await svc.GetCalendarsAsync(null, CancellationToken.None);
        Assert.Single(list); Assert.NotNull(list[0].OutlookCalendarBindingId);
    }
    [Fact] public async Task Cal_Update_Success()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var r=await svc.CreateCalendarAsync(new CreateCalendarRequest("Old", "#111111", null), CancellationToken.None);
        var u=await svc.UpdateCalendarAsync(r.Id, new CreateCalendarRequest("New", "#222222", null), CancellationToken.None);
        Assert.Equal("New", u.Name); Assert.Equal("#222222", u.Color);
    }
    [Fact] public async Task Cal_Update_NotFound_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.UpdateCalendarAsync(Guid.NewGuid(), new CreateCalendarRequest("X", null, null), CancellationToken.None));
    }
    [Fact] public async Task Cal_Delete_SoftDeletes()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var r=await svc.CreateCalendarAsync(new CreateCalendarRequest("Del", null, null), CancellationToken.None);
        await svc.DeleteCalendarAsync(r.Id, CancellationToken.None);
        var e=await db.Set<CalendarEntity>().IgnoreQueryFilters().SingleAsync(x=>x.Id==r.Id);
        Assert.NotNull(e.DeletedAt);
    }
    [Fact] public async Task Cal_Delete_NotFound_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.DeleteCalendarAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ---- Events Get ----
    [Fact] public async Task Events_GetEvents_OrdersByStart()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"B",null,null,D(2026,5,2,14),D(2026,5,2,15),null),CancellationToken.None);
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"A",null,null,D(2026,5,2,9),D(2026,5,2,10),null),CancellationToken.None);
        var evs=await svc.GetEventsAsync(D(2026,5,2,0),D(2026,5,3,0),CancellationToken.None);
        Assert.Equal(2,evs.Count); Assert.Equal("A",evs[0].Title);
    }
    [Fact] public async Task Events_GetEventsPaged_SearchAndCalendar_Filter()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Alpha",null,null,D(2026,6,1,9),D(2026,6,1,10),null),CancellationToken.None);
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Beta",null,null,D(2026,6,2,9),D(2026,6,2,10),null),CancellationToken.None);
        var page=await svc.GetEventsPagedAsync("Alpha",cal.Id,D(2026,6,1,0),D(2026,6,10,0),1,10,CancellationToken.None);
        Assert.Single(page.Items); Assert.Equal("Alpha",page.Items[0].Title);
    }
    [Fact] public async Task Events_GetEventsPaged_NoWindow_ReturnsAll()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"E1",null,null,D(2026,7,1,9),D(2026,7,1,10),null),CancellationToken.None);
        var paged=await svc.GetEventsPagedAsync(null,null,null,null,1,10,CancellationToken.None);
        Assert.Single(paged.Items);
    }
    [Fact] public async Task Events_GetEventEntities_Range()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"E",null,null,D(2026,8,1,9),D(2026,8,1,10),null),CancellationToken.None);
        var list=await svc.GetEventEntitiesAsync(D(2026,8,1,0),D(2026,8,2,0),CancellationToken.None);
        Assert.Single(list);
    }

    // ---- CreateEvent branches ----
    [Fact] public async Task CreateEvent_WithEmptyCalendarId_CreatesDefaultCalendar()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var r=await svc.CreateEventAsync(new CreateEventRequest(Guid.Empty,"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null),CancellationToken.None);
        Assert.NotEqual(Guid.Empty, r.CalendarId);
    }
    [Fact] public async Task CreateEvent_NonExistentCalendar_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(Guid.NewGuid(),"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null),CancellationToken.None));
    }
    [Fact] public async Task CreateEvent_OutlookBinding_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        AddOutlookBinding(db, cal.Id);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null),CancellationToken.None));
    }
    [Fact] public async Task CreateEvent_Html_Sanitized()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var r=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T","<p>hi</p><script>alert(1)</script>",null,D(2026,9,1,9),D(2026,9,1,10),null,DescriptionFormat:"html"),CancellationToken.None);
        Assert.DoesNotContain("script", r.Description ?? "", StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public async Task CreateEvent_ManualDesc_Dangerous_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T","<script>x</script>",null,D(2026,9,1,9),D(2026,9,1,10),null),CancellationToken.None));
    }
    [Fact] public async Task CreateEvent_InvalidEnum_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null,ShowAs:"invalid"),CancellationToken.None));
    }
    [Fact] public async Task CreateEvent_Exception_MissingFields_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null,IsException:true),CancellationToken.None));
    }
    [Fact] public async Task CreateEvent_Exception_Success()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var master=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Master",null,null,D(2026,1,5,10),D(2026,1,5,11),"FREQ=WEEKLY;COUNT=3"),CancellationToken.None);
        var ex=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Ex",null,null,D(2026,1,12,14),D(2026,1,12,15),null,IsException:true,SeriesMasterId:master.Id,RecurrenceId:D(2026,1,12,10).ToString("O")),CancellationToken.None);
        Assert.True(ex.IsException); Assert.Equal(master.Id, ex.SeriesMasterId);
    }
    [Fact] public async Task CreateEvent_InvalidAttachmentKind_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var att=new EventAttachmentReferenceDto("invalid","id","name");
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null,AttachmentReferences:new[]{att}),CancellationToken.None));
    }
    [Fact] public async Task CreateEvent_NegativeReminder_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,9,1,9),D(2026,9,1,10),null,IsReminderOn:true,ReminderMinutesBeforeStart:-5),CancellationToken.None));
    }

    // ---- UpdateEvent ----
    [Fact] public async Task UpdateEvent_Default_Success()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var e=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Old",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None);
        var u=await svc.UpdateEventAsync(e.Id,new UpdateEventRequest(cal.Id,"New",null,null,D(2026,4,1,11),D(2026,4,1,12),null),CancellationToken.None);
        Assert.Equal("New", u.Title);
    }
    [Fact] public async Task UpdateEvent_ScopeThis_CreatesException()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var master=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Weekly",null,null,D(2026,1,5,10),D(2026,1,5,11),"FREQ=WEEKLY;COUNT=4"),CancellationToken.None);
        var recId=D(2026,1,12,10).ToString("O");
        var ex=await svc.UpdateEventAsync(master.Id,new UpdateEventRequest(cal.Id,"Rescheduled",null,null,D(2026,1,12,14),D(2026,1,12,15),null,RecurrenceId:recId),"this",CancellationToken.None);
        Assert.True(ex.IsException);
    }
    [Fact] public async Task UpdateEvent_ScopeSeries_FromException_UpdatesMaster()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var master=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Weekly",null,null,D(2026,1,5,10),D(2026,1,5,11),"FREQ=WEEKLY;COUNT=4"),CancellationToken.None);
        var recId=D(2026,1,12,10).ToString("O");
        var ex=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Ex",null,null,D(2026,1,12,14),D(2026,1,12,15),null,IsException:true,SeriesMasterId:master.Id,RecurrenceId:recId),CancellationToken.None);
        var updated=await svc.UpdateEventAsync(ex.Id,new UpdateEventRequest(cal.Id,"SeriesNew",null,null,D(2026,1,5,10),D(2026,1,5,11),"FREQ=WEEKLY;COUNT=5"),"series",CancellationToken.None);
        Assert.Equal("SeriesNew", updated.Title);
    }
    [Fact] public async Task UpdateEvent_OutlookBinding_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var e=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None);
        var entity=await db.Set<EventEntity>().SingleAsync(x=>x.Id==e.Id);
        entity.OutlookCalendarBindingId=Guid.NewGuid();
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(()=>svc.UpdateEventAsync(e.Id,new UpdateEventRequest(cal.Id,"X",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None));
    }
    [Fact] public async Task UpdateEvent_MoveToOutlookCalendar_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal1=SeedCal(db); var cal2=SeedCal(db); await db.SaveChangesAsync();
        AddOutlookBinding(db, cal2.Id);
        var e=await svc.CreateEventAsync(new CreateEventRequest(cal1.Id,"T",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(()=>svc.UpdateEventAsync(e.Id,new UpdateEventRequest(cal2.Id,"T",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None));
    }
    [Fact] public async Task UpdateEvent_NotFound_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        await Assert.ThrowsAsync<DomainException>(()=>svc.UpdateEventAsync(Guid.NewGuid(),new UpdateEventRequest(cal.Id,"T",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None));
    }

    // ---- DeleteEvent ----
    [Fact] public async Task DeleteEvent_Single_SoftDelete()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var e=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Del",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None);
        await svc.DeleteEventAsync(e.Id,CancellationToken.None);
        var ent=await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(x=>x.Id==e.Id);
        Assert.NotNull(ent.DeletedAt);
    }
    [Fact] public async Task DeleteEvent_ScopeThis_OnMaster_CreatesCancelled()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var master=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"W",null,null,D(2026,1,5,10),D(2026,1,5,11),"FREQ=WEEKLY;COUNT=4"),CancellationToken.None);
        var recId=D(2026,1,12,10).ToString("O");
        await svc.DeleteEventAsync(master.Id,"this",recId,CancellationToken.None);
        var cancelled=await db.Set<EventEntity>().SingleAsync(x=>x.IsException && x.RecurrenceId==recId);
        Assert.Equal("CANCELLED", cancelled.Status);
    }
    [Fact] public async Task DeleteEvent_ScopeSeries_Cascades()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var master=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"W",null,null,D(2026,1,5,10),D(2026,1,5,11),"FREQ=WEEKLY;COUNT=2"),CancellationToken.None);
        var recId=D(2026,1,12,10).ToString("O");
        await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"Ex",null,null,D(2026,1,12,14),D(2026,1,12,15),null,IsException:true,SeriesMasterId:master.Id,RecurrenceId:recId),CancellationToken.None);
        await svc.DeleteEventAsync(master.Id,"series",CancellationToken.None);
        var m=await db.Set<EventEntity>().IgnoreQueryFilters().SingleAsync(x=>x.Id==master.Id);
        Assert.NotNull(m.DeletedAt);
    }
    [Fact] public async Task DeleteEvent_InvalidRecurrenceId_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var e=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"T",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(()=>svc.DeleteEventAsync(e.Id,"this","not-a-date",CancellationToken.None));
    }
    [Fact] public async Task DeleteEvent_NotFound_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.DeleteEventAsync(Guid.NewGuid(),CancellationToken.None));
    }
    [Fact] public async Task DeleteEvents_Batch()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var e1=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"A",null,null,D(2026,4,1,9),D(2026,4,1,10),null),CancellationToken.None);
        var e2=await svc.CreateEventAsync(new CreateEventRequest(cal.Id,"B",null,null,D(2026,4,2,9),D(2026,4,2,10),null),CancellationToken.None);
        var cnt=await svc.DeleteEventsAsync(new[]{e1.Id,e2.Id},CancellationToken.None);
        Assert.Equal(2,cnt);
    }

    // ---- Tasks ----
    [Fact] public async Task Tasks_Create_And_Get()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var cal=SeedCal(db); await db.SaveChangesAsync();
        var t=await svc.CreateTaskAsync(new CreateTaskRequest(cal.Id,"Task1","desc",1,"PT1H","PT30M",D(2026,5,10),D(2026,5,1)),CancellationToken.None);
        Assert.Equal("Task1", t.Title);
        var list=await svc.GetTasksAsync(null,CancellationToken.None);
        Assert.Single(list);
    }
    [Fact] public async Task Tasks_Create_Inbox_WhenNoCalendarOrStart()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var t=await svc.CreateTaskAsync(new CreateTaskRequest(null,"InboxTask",null,0,null,null,null,null),CancellationToken.None);
        Assert.True(t.IsInbox);
    }
    [Fact] public async Task Tasks_GetTasksPaged_Filters()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        await svc.CreateTaskAsync(new CreateTaskRequest(null,"Alpha",null,1,null,null,null,null),CancellationToken.None);
        await svc.CreateTaskAsync(new CreateTaskRequest(null,"Beta",null,2,null,null,null,null),CancellationToken.None);
        var paged=await svc.GetTasksPagedAsync(null,"Alpha",null,null,null,null,null,null,null,1,10,CancellationToken.None);
        Assert.Single(paged.Items); Assert.Equal("Alpha",paged.Items[0].Title);
    }
    [Fact] public async Task Tasks_Update_SetsCompleted()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var t=await svc.CreateTaskAsync(new CreateTaskRequest(null,"T",null,0,null,null,null,null),CancellationToken.None);
        var u=await svc.UpdateTaskAsync(t.Id,new UpdateTaskRequest(null,"T2","desc",2,null,null,null,null,Status:"COMPLETED"),CancellationToken.None);
        Assert.Equal("COMPLETED", u.Status);
    }
    [Fact] public async Task Tasks_PlanTask_SetsDates()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var t=await svc.CreateTaskAsync(new CreateTaskRequest(null,"PlanMe",null,0,null,null,null,null),CancellationToken.None);
        var p=await svc.PlanTaskAsync(t.Id,new PlanTaskRequest(D(2026,6,1,9),D(2026,6,1,10),null),CancellationToken.None);
        Assert.Equal(D(2026,6,1,9).ToUniversalTime(), p.DtStart);
    }
    [Fact] public async Task Tasks_BatchUpdate_ChangesStatus()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var t1=await svc.CreateTaskAsync(new CreateTaskRequest(null,"A",null,0,null,null,null,null),CancellationToken.None);
        var t2=await svc.CreateTaskAsync(new CreateTaskRequest(null,"B",null,0,null,null,null,null),CancellationToken.None);
        var res=await svc.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(new[]{t1.Id,t2.Id},"COMPLETED",null,null),CancellationToken.None);
        Assert.Equal(2, res.AffectedCount);
    }
    [Fact] public async Task Tasks_BatchUpdate_Empty_ReturnsZero()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var res=await svc.BatchUpdateTasksAsync(new BatchTaskUpdateRequest(Array.Empty<Guid>(),null,null,null),CancellationToken.None);
        Assert.Equal(0,res.AffectedCount);
    }

    // ---- Import ICS ----
    [Fact] public async Task ImportIcs_Empty_ReturnsZero()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var ics=new OutlookIcsService();
        var rep=await svc.ImportOutlookIcsAsync("",null,ics,CancellationToken.None);
        Assert.Equal(0,rep.Imported);
    }
    [Fact] public async Task ImportIcs_Valid_Single()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var ics=new OutlookIcsService();
        var cal=SeedCal(db); await db.SaveChangesAsync();
        string content="BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//\r\nBEGIN:VEVENT\r\nUID:test-uid@pim\r\nDTSTART:20260701T090000Z\r\nDTEND:20260701T100000Z\r\nSUMMARY:Imported Event\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var rep=await svc.ImportOutlookIcsAsync(content,cal.Id,ics,CancellationToken.None);
        Assert.Equal(1,rep.Imported);
    }
    [Fact] public async Task ImportIcs_Duplicate_Skipped()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateCalendarService(db);
        var ics=new OutlookIcsService();
        var cal=SeedCal(db); await db.SaveChangesAsync();
        string content="BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//test//\r\nBEGIN:VEVENT\r\nUID:dup@pim\r\nDTSTART:20260701T090000Z\r\nDTEND:20260701T100000Z\r\nSUMMARY:Dup\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        await svc.ImportOutlookIcsAsync(content,cal.Id,ics,CancellationToken.None);
        var rep2=await svc.ImportOutlookIcsAsync(content,cal.Id,ics,CancellationToken.None);
        Assert.Equal(1,rep2.Skipped);
    }

    // ---- Recurrence ----
    [Fact] public void Recurrence_Simple_WithinRange()
    {
        var svc=new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var e=new EventEntity{Id=Guid.NewGuid(), Title="S", DtStart=D(2026,3,1,9), DtEnd=D(2026,3,1,10)};
        var expanded=svc.ExpandEventsV2(new[]{e},D(2026,3,1,0),D(2026,3,2,0));
        Assert.Single(expanded);
    }
    [Fact] public void Recurrence_Simple_OutOfRange_Empty()
    {
        var svc=new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var e=new EventEntity{Id=Guid.NewGuid(), Title="S", DtStart=D(2026,3,10,9), DtEnd=D(2026,3,10,10)};
        var expanded=svc.ExpandEventsV2(new[]{e},D(2026,3,1,0),D(2026,3,2,0));
        Assert.Empty(expanded);
    }
    [Fact] public void Recurrence_Daily_Expand()
    {
        var svc=new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var e=new EventEntity{Id=Guid.NewGuid(), Title="D", DtStart=D(2026,3,1,9), DtEnd=D(2026,3,1,10), RRule="FREQ=DAILY;COUNT=3", IsSeriesMaster=true};
        var expanded=svc.ExpandEventsV2(new[]{e},D(2026,3,1,0),D(2026,3,10,0));
        Assert.Equal(3,expanded.Count);
    }
    [Fact] public void Recurrence_ExDates_Skips()
    {
        var svc=new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var ex=D(2026,3,2,9).ToString("O");
        var e=new EventEntity{Id=Guid.NewGuid(), Title="D", DtStart=D(2026,3,1,9), DtEnd=D(2026,3,1,10), RRule="FREQ=DAILY;COUNT=3", IsSeriesMaster=true, ExDatesJson=System.Text.Json.JsonSerializer.Serialize(new[]{ex})};
        var expanded=svc.ExpandEventsV2(new[]{e},D(2026,3,1,0),D(2026,3,10,0));
        Assert.Equal(2,expanded.Count);
    }
    [Fact] public void Recurrence_Exception_Overlay()
    {
        var svc=new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var masterId=Guid.NewGuid();
        var master=new EventEntity{Id=masterId, Title="W", DtStart=D(2026,1,5,10), DtEnd=D(2026,1,5,11), RRule="FREQ=WEEKLY;COUNT=2", IsSeriesMaster=true};
        var recId=D(2026,1,12,10).ToString("O");
        var ex=new EventEntity{Id=Guid.NewGuid(), Title="Ex", DtStart=D(2026,1,12,14), DtEnd=D(2026,1,12,15), IsException=true, SeriesMasterId=masterId, RecurrenceId=recId};
        var expanded=svc.ExpandEventsV2(new[]{master, ex},D(2026,1,1),D(2026,2,1));
        Assert.Equal(2,expanded.Count);
        Assert.Contains(expanded, x=>x.IsException);
    }
    [Fact] public void Recurrence_Cancelled_IsMarked()
    {
        var svc=new RecurrenceService(NullLogger<RecurrenceService>.Instance);
        var masterId=Guid.NewGuid();
        var master=new EventEntity{Id=masterId, Title="W", DtStart=D(2026,1,5,10), DtEnd=D(2026,1,5,11), RRule="FREQ=WEEKLY;COUNT=2", IsSeriesMaster=true};
        var recId=D(2026,1,12,10).ToString("O");
        var ex=new EventEntity{Id=Guid.NewGuid(), Title="Cancelled", DtStart=D(2026,1,12,10), DtEnd=D(2026,1,12,11), IsException=true, SeriesMasterId=masterId, RecurrenceId=recId, Status="CANCELLED"};
        var expanded=svc.ExpandEventsV2(new[]{master, ex},D(2026,1,1),D(2026,2,1));
        var cancelled=expanded.Single(x=>x.IsException);
        Assert.True(cancelled.IsCancelled);
    }

    // ---- ReminderService ----
    [Fact] public async Task Reminder_Create_List_Snooze_Dismiss()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateReminderService(db);
        var r=await svc.CreateAsync(new CreateReminderRequest("object",Guid.NewGuid(),"Title","Body","trig","L1LowRiskAction",new[]{"Web"},null,null,D(2026,7,8,9)),CancellationToken.None);
        var list=await svc.ListAsync(CancellationToken.None);
        Assert.Single(list);
        var snoozed=await svc.SnoozeAsync(r.Id,D(2026,7,9,10),CancellationToken.None);
        Assert.Equal("Snoozed", snoozed.Status);
        var dismissed=await svc.DismissAsync(r.Id,CancellationToken.None);
        Assert.Equal("Dismissed", dismissed.Status);
    }
    [Fact] public async Task Reminder_Create_InvalidTitle_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateReminderService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateAsync(new CreateReminderRequest("object",Guid.NewGuid(),"", "Body","trig","L1LowRiskAction",new[]{"Web"},null,null,D(2026,7,8,9)),CancellationToken.None));
    }
    [Fact] public async Task Reminder_HandleAction_HighRisk_RequiresDetail()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateReminderService(db);
        var r=await svc.CreateAsync(new CreateReminderRequest("object",Guid.NewGuid(),"High","Body","trig","L2PimFactChange",new[]{"Web"},null,null,D(2026,7,8,9)),CancellationToken.None);
        var resp=await svc.HandleActionAsync(r.Id,"confirm",CancellationToken.None);
        Assert.Equal("OpenDetailRequired", resp.Kind);
    }
    [Fact] public async Task Reminder_HandleAction_LowRisk_Dismiss()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateReminderService(db);
        var r=await svc.CreateAsync(new CreateReminderRequest("object",Guid.NewGuid(),"Low","Body","trig","L1LowRiskAction",new[]{"Web"},null,null,D(2026,7,8,9)),CancellationToken.None);
        var resp=await svc.HandleActionAsync(r.Id,"dismiss",CancellationToken.None);
        Assert.Equal("Executed", resp.Kind); Assert.Equal("Dismissed", resp.Status);
    }
    [Fact] public async Task Reminder_BuildPayload_And_DeliveryLog()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateReminderService(db);
        var r=await svc.CreateAsync(new CreateReminderRequest("confirmation",Guid.NewGuid(),"T","B","trig","L1LowRiskAction",new[]{"Web"},null,null,D(2026,7,8,9)),CancellationToken.None);
        var payload=await svc.BuildNotificationPayloadAsync(r.Id,"Web",CancellationToken.None);
        Assert.Equal(r.Id, payload.ReminderId);
        var log=await svc.GetDeliveryLogAsync(CancellationToken.None);
        Assert.NotEmpty(log);
    }
    [Fact] public async Task Reminder_HandleAction_Snooze_Shifts()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreateReminderService(db);
        var r=await svc.CreateAsync(new CreateReminderRequest("object",Guid.NewGuid(),"Low","Body","trig","L1LowRiskAction",new[]{"Web"},null,null,D(2026,7,8,9)),CancellationToken.None);
        var before=DateTimeOffset.UtcNow;
        var resp=await svc.HandleActionAsync(r.Id,"snooze",CancellationToken.None);
        Assert.Equal("Executed", resp.Kind);
        var updated=(await svc.ListAsync(CancellationToken.None)).Single(x=>x.Id==r.Id);
        Assert.Equal("Snoozed", updated.Status);
        Assert.True(updated.ScheduledAt>before);
    }

    // ---- ReportService ----
    [Fact] public async Task Report_Generate_Daily()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        var rep=await svc.GenerateAsync(new GenerateReportRequest("Daily", DateOnly.FromDateTime(D(2026,7,8).UtcDateTime), null),CancellationToken.None);
        Assert.Equal("Daily", rep.Kind); Assert.Equal("Active", rep.Status);
    }
    [Fact] public async Task Report_Generate_Weekly()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        var rep=await svc.GenerateAsync(new GenerateReportRequest("Weekly", DateOnly.FromDateTime(D(2026,7,8).UtcDateTime), null),CancellationToken.None);
        Assert.Equal("Weekly", rep.Kind);
    }
    [Fact] public async Task Report_Generate_InvalidKind_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        await Assert.ThrowsAsync<DomainException>(()=>svc.GenerateAsync(new GenerateReportRequest("Yearly", DateOnly.FromDateTime(D(2026,7,8).UtcDateTime), null),CancellationToken.None));
    }
    [Fact] public async Task Report_List_And_Get_And_Archive()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        var rep=await svc.GenerateAsync(new GenerateReportRequest("Daily", DateOnly.FromDateTime(D(2026,7,8).UtcDateTime), null),CancellationToken.None);
        var list=await svc.ListAsync(CancellationToken.None);
        Assert.Contains(list, x=>x.Id==rep.Id);
        var got=await svc.GetAsync(rep.Id,CancellationToken.None);
        Assert.Equal(rep.Id, got.Id);
        var archived=await svc.ArchiveAsync(rep.Id,CancellationToken.None);
        Assert.Equal("Archived", archived.Status);
    }
    [Fact] public async Task Report_Get_NotFound_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        await Assert.ThrowsAsync<DomainException>(()=>svc.GetAsync(Guid.NewGuid(),CancellationToken.None));
    }
    [Fact] public async Task Report_RequestSuggestion_NotFound_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=new ReportService(db, ServiceTestBase.CurrentUser(), new OperationConfirmationService(db));
        await Assert.ThrowsAsync<DomainException>(()=>svc.RequestSuggestionActionAsync(Guid.NewGuid(),CancellationToken.None));
    }

    // ---- PlanningModelService ----
    [Fact] public async Task Planning_GetLayers_Events()
    {
        await using var db=ServiceTestBase.CreateDb();
        var cal=SeedCal(db); await db.SaveChangesAsync();
        db.Set<EventEntity>().Add(new EventEntity{CalendarId=cal.Id, Uid=Guid.NewGuid()+"@pim", Title="Ev", DtStart=D(2026,5,1,9), DtEnd=D(2026,5,1,10)});
        await db.SaveChangesAsync();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var res=await svc.GetCalendarLayersAsync(new CalendarLayerQuery(D(2026,5,1,0),D(2026,5,2,0),new[]{"events"}),CancellationToken.None);
        Assert.Single(res.Items);
    }
    [Fact] public async Task Planning_GetLayers_InvalidRange_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.GetCalendarLayersAsync(new CalendarLayerQuery(D(2026,5,2),D(2026,5,1),null),CancellationToken.None));
    }
    [Fact] public async Task Planning_GetLayers_DefaultLayers_WhenNull()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var res=await svc.GetCalendarLayersAsync(new CalendarLayerQuery(D(2026,5,1),D(2026,5,2),null),CancellationToken.None);
        Assert.NotNull(res.Items);
    }
    [Fact] public async Task Planning_ListProjects_Empty()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var list=await svc.ListProjectsAsync(CancellationToken.None);
        Assert.Empty(list);
    }
    [Fact] public async Task Planning_CreateProject_Success()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var p=await svc.CreateProjectAsync(new CreateDomainProjectRequest("Proj","desc",null),CancellationToken.None);
        Assert.Equal("Proj", p.Name);
        var list=await svc.ListProjectsAsync(CancellationToken.None);
        Assert.Single(list);
    }
    [Fact] public async Task Planning_CreateProject_EmptyName_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateProjectAsync(new CreateDomainProjectRequest("","desc",null),CancellationToken.None));
    }
    [Fact] public async Task Planning_TaskBooks_CreateAndList()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var b=await svc.CreateTaskBookAsync(new CreateTaskBookRequest(null,"Book1",null,null),CancellationToken.None);
        Assert.Equal("Book1", b.Name);
        var list=await svc.ListTaskBooksAsync(CancellationToken.None);
        Assert.Single(list);
    }
    [Fact] public async Task Planning_TaskBooks_CreateWithInvalidProject_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateTaskBookAsync(new CreateTaskBookRequest(Guid.NewGuid(),"Book",null,null),CancellationToken.None));
    }
    [Fact] public async Task Planning_Checklist_Add()
    {
        await using var db=ServiceTestBase.CreateDb();
        var calSvc=ServiceTestBase.CreateCalendarService(db);
        var task=await calSvc.CreateTaskAsync(new CreateTaskRequest(null,"Task",null,0,null,null,null,null),CancellationToken.None);
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var item=await svc.AddChecklistItemAsync(task.Id, new AddTaskChecklistItemRequest("Item1",null),CancellationToken.None);
        Assert.Equal("Item1", item.Title);
    }
    [Fact] public async Task Planning_Habits_CreateAndList()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var h=await svc.CreateHabitAsync(new CreateHabitRequest("Habit1","desc","Daily","manual","Active",null),CancellationToken.None);
        Assert.Equal("Habit1", h.Title);
        var list=await svc.ListHabitsAsync(CancellationToken.None);
        Assert.Single(list);
    }
    [Fact] public async Task Planning_HabitOccurrence_Create()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var h=await svc.CreateHabitAsync(new CreateHabitRequest("H","desc",null,null,null,null),CancellationToken.None);
        var occ=await svc.CreateHabitOccurrenceAsync(h.Id, new CreateHabitOccurrenceRequest(D(2026,5,1,9),D(2026,5,1,10),null,null),CancellationToken.None);
        Assert.Equal(h.Id, occ.HabitRoutineId);
    }
    [Fact] public async Task Planning_HabitOccurrence_InvalidRange_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var h=await svc.CreateHabitAsync(new CreateHabitRequest("H","desc",null,null,null,null),CancellationToken.None);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateHabitOccurrenceAsync(h.Id, new CreateHabitOccurrenceRequest(D(2026,5,1,10),D(2026,5,1,9),null,null),CancellationToken.None));
    }
    [Fact] public async Task Planning_Availability_CreateAndList()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var av=await svc.CreateAvailabilityWindowAsync(new CreateAvailabilityWindowRequest("Avail",D(2026,5,1,9),D(2026,5,1,10),null,null),CancellationToken.None);
        Assert.NotEqual(Guid.Empty, av.Id);
        var list=await svc.ListAvailabilityAsync(CancellationToken.None);
        Assert.Single(list);
    }
    [Fact] public async Task Planning_Availability_InvalidRange_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateAvailabilityWindowAsync(new CreateAvailabilityWindowRequest("A",D(2026,5,1,10),D(2026,5,1,9),null,null),CancellationToken.None));
    }
    [Fact] public async Task Planning_AiPlaceholder_CreateAndConfirm()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var ph=await svc.CreateAiPlaceholderAsync(new CreateAiPlanningPlaceholderRequest("AI",D(2026,5,1,9),D(2026,5,1,10),"reason",null),CancellationToken.None);
        Assert.NotEqual(Guid.Empty, ph.Id);
        var conf=await svc.ConfirmAiPlaceholderAsync(ph.Id,CancellationToken.None);
        Assert.NotEqual(Guid.Empty, conf.Id);
    }
    [Fact] public async Task Planning_AiPlaceholder_InvalidRange_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateAiPlaceholderAsync(new CreateAiPlanningPlaceholderRequest("AI",D(2026,5,1,10),D(2026,5,1,9),"reason",null),CancellationToken.None));
    }
    [Fact] public async Task Planning_Segment_CreateListDelete()
    {
        await using var db=ServiceTestBase.CreateDb();
        var taskEnt=new TaskEntity{UserId=ServiceTestBase.DefaultUserId, Title="T", Uid=Guid.NewGuid()+"@pim"};
        db.Set<TaskEntity>().Add(taskEnt); await db.SaveChangesAsync();
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        var seg=await svc.CreateSegmentAsync(taskEnt.Id, new CreateTaskExecutionSegmentRequest(D(2026,5,1,9),D(2026,5,1,10),"Planned","manual",null),CancellationToken.None);
        Assert.Equal(taskEnt.Id, seg.TaskId);
        var cnt=await db.Set<TaskExecutionSegmentEntity>().CountAsync();
        Assert.Equal(1,cnt);
        await svc.DeleteSegmentAsync(taskEnt.Id,seg.Id,CancellationToken.None);
        var ent=await db.Set<TaskExecutionSegmentEntity>().IgnoreQueryFilters().SingleAsync(x=>x.Id==seg.Id);
        Assert.NotNull(ent.DeletedAt);
    }
    [Fact] public async Task Planning_Segment_InvalidRange_Throws()
    {
        await using var db=ServiceTestBase.CreateDb();
        var calSvc=ServiceTestBase.CreateCalendarService(db);
        var task=await calSvc.CreateTaskAsync(new CreateTaskRequest(null,"T",null,0,null,null,null,null),CancellationToken.None);
        var svc=ServiceTestBase.CreatePlanningModelService(db);
        await Assert.ThrowsAsync<DomainException>(()=>svc.CreateSegmentAsync(task.Id, new CreateTaskExecutionSegmentRequest(D(2026,5,1,10),D(2026,5,1,9),"Planned","manual",null),CancellationToken.None));
    }
}
