import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PageSideComponent } from './page-side.component';

describe('PageSideComponent', () => {
  let component: PageSideComponent;
  let fixture: ComponentFixture<PageSideComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PageSideComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(PageSideComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
